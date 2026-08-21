using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using JameX.Contracts;
using JameX.Ingest.Configuration;
using JameX.Ingest.Domain;
using Microsoft.Extensions.Options;

namespace JameX.Ingest.Repositories;

/// <summary>
/// Every way Ingest reaches <c>jamex-upload-sessions</c>, and nothing else.
/// </summary>
public interface IUploadSessionRepository
{
    /// <summary>Creates a session. Fails if the id already exists.</summary>
    Task CreateAsync(UploadSession session, CancellationToken ct);

    Task<UploadSession?> GetAsync(Guid uploadId, CancellationToken ct);

    /// <summary>
    /// Records one confirmed part. Safe to call concurrently — see the
    /// implementation for why this is an <c>UpdateItem</c> and not a read,
    /// modify and write.
    /// </summary>
    Task RecordPartAsync(Guid uploadId, int partNumber, string eTag, CancellationToken ct);

    Task SetStatusAsync(Guid uploadId, UploadStatus status, CancellationToken ct);

    /// <summary>
    /// Moves the session to Completed and stamps the event id that will be
    /// published for it. Returns <c>false</c> if it was already completed — in
    /// which case this is a retry, and the caller should reuse the stored id
    /// rather than mint a new one.
    /// </summary>
    Task<bool> TryMarkCompletedAsync(Guid uploadId, Guid completionEventId, CancellationToken ct);
}

internal sealed class UploadSessionRepository(
    IAmazonDynamoDB dynamo,
    IOptions<UploadOptions> options) : IUploadSessionRepository
{
    private readonly string _table = options.Value.SessionsTableName;

    public async Task CreateAsync(UploadSession session, CancellationToken ct)
    {
        await dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _table,
            Item = ToItem(session),
            // Refuses to silently overwrite an existing session. Without this a
            // repeated request could reset the recorded parts of an upload
            // already in flight, and the client would lose its progress.
            ConditionExpression = "attribute_not_exists(uploadId)"
        }, ct);
    }

    public async Task<UploadSession?> GetAsync(Guid uploadId, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _table,
            Key = Key(uploadId),
            // Strongly consistent: the client may ask for progress immediately
            // after a part lands, and an eventually consistent read could report
            // the part missing and prompt a pointless re-upload.
            ConsistentRead = true
        }, ct);

        return response.IsItemSet ? FromItem(response.Item) : null;
    }

    /// <summary>
    /// A single atomic update of one key inside the <c>parts</c> map.
    /// <para>
    /// The obvious alternative — read the session, add the part, write it back —
    /// is a lost-update race. A browser uploads several parts in parallel, so
    /// two completions overlap: both read a map with 4 entries, each adds its
    /// own, and each writes back 5. One ETag is silently gone, and the upload
    /// can never be completed because S3 requires the full set.
    /// </para>
    /// <para>
    /// <c>SET #parts.#n = :etag</c> mutates one attribute path server-side, so
    /// concurrent calls for different parts cannot interfere at all.
    /// </para>
    /// </summary>
    public async Task RecordPartAsync(Guid uploadId, int partNumber, string eTag, CancellationToken ct)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _table,
            Key = Key(uploadId),
            UpdateExpression = "SET #parts.#n = :etag",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#parts"] = "parts",
                ["#n"] = partNumber.ToString(),
                ["#status"] = "status"
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":etag"] = new(eTag),
                [":inProgress"] = new() { N = ((int)UploadStatus.InProgress).ToString() }
            },
            // Reject a part for a session that has expired, been aborted, or
            // already completed — S3 would refuse it anyway, and recording it
            // would leave the session lying about its own state.
            ConditionExpression = "attribute_exists(uploadId) AND #status = :inProgress"
        }, ct);
    }

    public async Task SetStatusAsync(Guid uploadId, UploadStatus status, CancellationToken ct)
    {
        await dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _table,
            Key = Key(uploadId),
            UpdateExpression = "SET #status = :status",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new() { N = ((int)status).ToString() }
            },
            ConditionExpression = "attribute_exists(uploadId)"
        }, ct);
    }

    public async Task<bool> TryMarkCompletedAsync(
        Guid uploadId, Guid completionEventId, CancellationToken ct)
    {
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _table,
                Key = Key(uploadId),
                UpdateExpression = "SET #status = :completed, completionEventId = :eventId",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":completed"] = new() { N = ((int)UploadStatus.Completed).ToString() },
                    [":eventId"] = new(completionEventId.ToString()),
                    [":inProgress"] = new() { N = ((int)UploadStatus.InProgress).ToString() }
                },
                // Only the first completion wins. A concurrent or repeated call
                // fails this condition, which is how the caller learns to reuse
                // the already-stored event id instead of minting a second one.
                ConditionExpression = "attribute_exists(uploadId) AND #status = :inProgress"
            }, ct);

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private static Dictionary<string, AttributeValue> Key(Guid uploadId) =>
        new() { ["uploadId"] = new(uploadId.ToString()) };

    private static Dictionary<string, AttributeValue> ToItem(UploadSession s)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["uploadId"] = new(s.UploadId.ToString()),
            ["videoId"] = new(s.VideoId.ToString()),
            ["uploaderId"] = new(s.UploaderId.ToString()),
            ["channelId"] = new(s.ChannelId.ToString()),
            ["s3UploadId"] = new(s.S3UploadId),
            ["objectKey"] = new(s.ObjectKey),
            ["fileName"] = new(s.FileName),
            ["contentType"] = new(s.ContentType),
            ["totalBytes"] = Number(s.TotalBytes),
            ["partSizeBytes"] = Number(s.PartSizeBytes),
            ["totalParts"] = Number(s.TotalParts),
            ["title"] = new(s.Title),
            ["defaultLanguage"] = new(s.DefaultLanguage),
            ["privacy"] = Number((int)s.Privacy),
            ["status"] = Number((int)s.Status),
            ["createdAt"] = new(s.CreatedAt.ToString("O")),

            // TTL must be a Number in epoch SECONDS. Milliseconds — the usual
            // slip — puts expiry roughly 50,000 years out and the row never
            // disappears.
            ["expiresAt"] = Number(s.ExpiresAt.ToUnixTimeSeconds()),

            // An empty map, populated one key at a time by RecordPartAsync. It
            // has to exist up front, because `SET parts.#n = :etag` fails if the
            // parent attribute is absent.
            ["parts"] = new() { M = new Dictionary<string, AttributeValue>() },

            // A List, not a String Set: DynamoDB rejects empty sets, and a video
            // with no tags is perfectly normal.
            ["tags"] = new() { L = s.Tags.Select(t => new AttributeValue(t)).ToList() }
        };

        if (s.Description is not null) item["description"] = new AttributeValue(s.Description);
        if (s.CategoryId is not null) item["categoryId"] = new AttributeValue(s.CategoryId);

        return item;
    }

    private static UploadSession FromItem(Dictionary<string, AttributeValue> item) => new()
    {
        UploadId = Guid.Parse(item["uploadId"].S),
        VideoId = Guid.Parse(item["videoId"].S),
        UploaderId = Guid.Parse(item["uploaderId"].S),
        ChannelId = Guid.Parse(item["channelId"].S),
        S3UploadId = item["s3UploadId"].S,
        ObjectKey = item["objectKey"].S,
        FileName = item["fileName"].S,
        ContentType = item["contentType"].S,
        TotalBytes = long.Parse(item["totalBytes"].N),
        PartSizeBytes = long.Parse(item["partSizeBytes"].N),
        TotalParts = int.Parse(item["totalParts"].N),
        Title = item["title"].S,
        Description = item.TryGetValue("description", out var d) ? d.S : null,
        CategoryId = item.TryGetValue("categoryId", out var c) ? c.S : null,
        Tags = item.TryGetValue("tags", out var t) ? t.L.Select(x => x.S).ToArray() : [],
        DefaultLanguage = item["defaultLanguage"].S,
        Privacy = (VideoPrivacy)int.Parse(item["privacy"].N),
        Status = (UploadStatus)int.Parse(item["status"].N),
        CreatedAt = DateTimeOffset.Parse(item["createdAt"].S),
        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(item["expiresAt"].N)),
        UploadedParts = item.TryGetValue("parts", out var p)
            ? p.M.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value.S)
            : [],
        CompletionEventId = item.TryGetValue("completionEventId", out var e) ? Guid.Parse(e.S) : null
    };

    private static AttributeValue Number(long value) => new() { N = value.ToString() };
}
