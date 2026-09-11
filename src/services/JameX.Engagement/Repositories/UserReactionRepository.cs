using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using JameX.Contracts;
using JameX.Engagement.Configuration;
using Microsoft.Extensions.Options;

namespace JameX.Engagement.Repositories;

/// <summary>
/// Every way Engagement reaches <c>jamex-user-reactions</c>.
/// <para>
/// One row per (user, video) is the entire idempotency mechanism for
/// like/dislike — see <c>ReactionService</c> (Module 3). This repository just
/// stores and retrieves that row; deciding what a state <i>transition</i>
/// means for the counters is a service-layer job, not this one's.
/// </para>
/// </summary>
public interface IUserReactionRepository
{
    /// <summary>Null means no reaction — absence of a row <i>is</i> "none", never a stored value.</summary>
    Task<ReactionKind?> GetAsync(Guid userId, Guid videoId, CancellationToken ct);

    /// <summary>Upserts the caller's reaction. A second call with the same kind is a harmless no-op.</summary>
    Task PutAsync(Guid userId, Guid videoId, ReactionKind kind, CancellationToken ct);

    /// <summary>Withdraws a reaction — the row is deleted, not set to some "none" value, for the same reason <see cref="ReactionKind"/> has no None member.</summary>
    Task RemoveAsync(Guid userId, Guid videoId, CancellationToken ct);

    /// <summary>
    /// Every reaction recorded against a video, for the <c>VideoDeleted</c>
    /// teardown. Reached through the <c>by-video</c> GSI — the base table is
    /// keyed by user first, so finding every reaction to one video would
    /// otherwise mean scanning the whole table.
    /// </summary>
    Task DeleteAllForVideoAsync(Guid videoId, CancellationToken ct);
}

internal sealed class UserReactionRepository(
    IAmazonDynamoDB dynamo,
    IOptions<EngagementOptions> options) : IUserReactionRepository
{
    private readonly string _table = options.Value.ReactionsTableName;

    public async Task<ReactionKind?> GetAsync(Guid userId, Guid videoId, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _table,
            Key = Key(userId, videoId),
            ConsistentRead = true
        }, ct);

        return response.IsItemSet
            ? (ReactionKind)int.Parse(response.Item["reactionKind"].N)
            : null;
    }

    public Task PutAsync(Guid userId, Guid videoId, ReactionKind kind, CancellationToken ct) =>
        dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _table,
            Item = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new(userId.ToString()),
                ["videoId"] = new(videoId.ToString()),
                ["reactionKind"] = new() { N = ((int)kind).ToString() },
                ["reactedAt"] = new(DateTimeOffset.UtcNow.ToString("O"))
            }
        }, ct);

    public Task RemoveAsync(Guid userId, Guid videoId, CancellationToken ct) =>
        dynamo.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _table,
            Key = Key(userId, videoId)
        }, ct);

    public async Task DeleteAllForVideoAsync(Guid videoId, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(new QueryRequest
        {
            TableName = _table,
            IndexName = "by-video",
            KeyConditionExpression = "videoId = :videoId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":videoId"] = new(videoId.ToString())
            },
            ProjectionExpression = "userId, videoId"
        }, ct);

        if (response.Items.Count == 0) return;

        foreach (var chunk in response.Items.Chunk(25))
        {
            await dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    // Deletes must go through the BASE table's key (userId +
                    // videoId), never the GSI — a GSI has no delete API of its
                    // own, it only exists to make this query possible.
                    [_table] = chunk.Select(item => new WriteRequest
                    {
                        DeleteRequest = new DeleteRequest { Key = item }
                    }).ToList()
                }
            }, ct);
        }
    }

    private static Dictionary<string, AttributeValue> Key(Guid userId, Guid videoId) => new()
    {
        ["userId"] = new(userId.ToString()),
        ["videoId"] = new(videoId.ToString())
    };
}
