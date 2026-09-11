using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using JameX.Engagement.Configuration;
using Microsoft.Extensions.Options;

namespace JameX.Engagement.Repositories;

/// <summary>Views, likes and dislikes as read back from DynamoDB — comments are not this repository's concern.</summary>
public sealed record CounterSnapshot(long Views, long Likes, long Dislikes);

/// <summary>
/// Every way Engagement reaches <c>jamex-video-counters</c>.
/// <para>
/// One table, two different counting strategies in it — see
/// <see cref="EngagementOptions.ViewShardCount"/> for why views are sharded
/// and likes/dislikes are not. Both strategies share the same underlying
/// trick: DynamoDB's <c>ADD</c> update is a genuine atomic increment at the
/// server, so two concurrent view pings for the same shard cannot race each
/// other into a lost update the way a read-then-write would.
/// </para>
/// </summary>
public interface IVideoCounterRepository
{
    /// <summary>
    /// Sets likes and dislikes to zero explicitly, so a freshly encoded video
    /// has a determinate state to read and delete. View shards are
    /// deliberately <b>not</b> pre-created here — see <see cref="RecordViewAsync"/>.
    /// </summary>
    Task InitializeAsync(Guid videoId, CancellationToken ct);

    /// <summary>
    /// Atomically increments one randomly chosen view shard.
    /// <para>
    /// The shard is created on first write — DynamoDB's <c>ADD</c> auto-vivifies
    /// an absent item, so there is nothing to pre-allocate. A video with three
    /// views might have hit only two of ten shards, and that is fine:
    /// <see cref="GetAsync"/> sums whatever shards actually exist.
    /// </para>
    /// </summary>
    Task RecordViewAsync(Guid videoId, CancellationToken ct);

    /// <summary>Atomically adds <paramref name="delta"/> to the likes counter — negative to undo one.</summary>
    Task AdjustLikesAsync(Guid videoId, int delta, CancellationToken ct);

    /// <summary>Atomically adds <paramref name="delta"/> to the dislikes counter.</summary>
    Task AdjustDislikesAsync(Guid videoId, int delta, CancellationToken ct);

    /// <summary>
    /// Reads every counter row for a video in one <c>Query</c> — the whole
    /// reason views and likes/dislikes share a partition key — and sums the
    /// view shards in memory.
    /// </summary>
    Task<CounterSnapshot> GetAsync(Guid videoId, CancellationToken ct);

    /// <summary>Removes every counter row for a video — the <c>VideoDeleted</c> teardown.</summary>
    Task DeleteAllAsync(Guid videoId, CancellationToken ct);
}

internal sealed class VideoCounterRepository(
    IAmazonDynamoDB dynamo,
    IOptions<EngagementOptions> options) : IVideoCounterRepository
{
    private const string LikesKey = "LIKES";
    private const string DislikesKey = "DISLIKES";
    private const string ViewShardPrefix = "VIEWS#";

    private readonly string _table = options.Value.CountersTableName;
    private readonly int _shardCount = options.Value.ViewShardCount;

    public async Task InitializeAsync(Guid videoId, CancellationToken ct)
    {
        await Task.WhenAll(
            PutZeroAsync(videoId, LikesKey, ct),
            PutZeroAsync(videoId, DislikesKey, ct));
    }

    private async Task PutZeroAsync(Guid videoId, string counterKey, CancellationToken ct)
    {
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = _table,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["videoId"] = new(videoId.ToString()),
                    ["counterKey"] = new(counterKey),
                    ["value"] = new() { N = "0" }
                },
                // Conditioned on absence, not a plain overwrite: a redelivered
                // VideoEncoded must not reset a counter that real traffic has
                // already moved off zero. This is what makes InitializeAsync
                // safe to call twice, which SqsEventConsumerService's
                // at-least-once delivery means it eventually will be.
                ConditionExpression = "attribute_not_exists(videoId)"
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Already initialised — a prior delivery got there first.
        }
    }

    public Task RecordViewAsync(Guid videoId, CancellationToken ct) =>
        AddAsync(videoId, $"{ViewShardPrefix}{Random.Shared.Next(_shardCount)}", 1, ct);

    public Task AdjustLikesAsync(Guid videoId, int delta, CancellationToken ct) =>
        AddAsync(videoId, LikesKey, delta, ct);

    public Task AdjustDislikesAsync(Guid videoId, int delta, CancellationToken ct) =>
        AddAsync(videoId, DislikesKey, delta, ct);

    private Task AddAsync(Guid videoId, string counterKey, int delta, CancellationToken ct) =>
        dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _table,
            Key = new Dictionary<string, AttributeValue>
            {
                ["videoId"] = new(videoId.ToString()),
                ["counterKey"] = new(counterKey)
            },
            // ADD is DynamoDB's atomic increment: read, modify and write happen
            // as one operation at the server, so concurrent view pings for the
            // same shard cannot overwrite each other's delta. A plain GetItem +
            // PutItem here would lose updates under real traffic.
            UpdateExpression = "ADD #value :delta",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#value"] = "value" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":delta"] = new() { N = delta.ToString() }
            }
        }, ct);

    public async Task<CounterSnapshot> GetAsync(Guid videoId, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = _table,
            KeyConditionExpression = "videoId = :videoId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":videoId"] = new(videoId.ToString())
            },
            // Strongly consistent: a viewer who just liked a video and
            // immediately reloads the page should see their own like counted,
            // not a stale replica that has not caught up yet.
            ConsistentRead = true
        }, ct);

        long views = 0, likes = 0, dislikes = 0;

        foreach (var item in response.Items)
        {
            var key = item["counterKey"].S;
            var value = long.Parse(item["value"].N);

            if (key.StartsWith(ViewShardPrefix, StringComparison.Ordinal)) views += value;
            else if (key == LikesKey) likes = value;
            else if (key == DislikesKey) dislikes = value;
        }

        return new CounterSnapshot(views, likes, dislikes);
    }

    public async Task DeleteAllAsync(Guid videoId, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = _table,
            KeyConditionExpression = "videoId = :videoId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":videoId"] = new(videoId.ToString())
            },
            // Only the key columns are needed to delete each item.
            ProjectionExpression = "videoId, counterKey"
        }, ct);

        if (response.Items.Count == 0) return;

        // BatchWriteItem caps at 25 requests per call. A video's counter rows
        // are at most (shard count + 2) — well under that in practice — but
        // chunking keeps this correct even if the shard count is turned up.
        foreach (var chunk in response.Items.Chunk(25))
        {
            await dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    [_table] = chunk.Select(item => new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest { Key = item }
                    }).ToList()
                }
            }, ct);
        }
    }
}
