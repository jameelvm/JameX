using JameX.Contracts;

namespace JameX.Ingest.Domain;

/// <summary>
/// One in-flight upload.
/// <para>
/// Lives in DynamoDB rather than Postgres for three reasons. It is written on
/// every completed part — hundreds of small updates per upload, which is a
/// write pattern DynamoDB is built for and a relational row is not. It is
/// always read by primary key, so nothing here needs joins or ordering. And it
/// is <b>temporary</b>: DynamoDB's TTL removes abandoned sessions with no sweep
/// job to write, monitor or forget about.
/// </para>
/// <para>
/// This is chapter 3's "server retains data temporarily to allow resumption" —
/// the record that lets a dropped connection resume instead of restarting a
/// 600 MB transfer.
/// </para>
/// </summary>
public sealed class UploadSession
{
    /// <summary>Partition key. Identifies the session, not the video.</summary>
    public required Guid UploadId { get; init; }

    /// <summary>
    /// Minted here, before a single byte arrives, and handed straight back to
    /// the client so it can poll for progress. The raw S3 object key embeds it,
    /// and Catalog later stores the row under this same id — which is why
    /// nothing downstream is allowed to generate its own.
    /// </summary>
    public required Guid VideoId { get; init; }

    public required Guid UploaderId { get; init; }
    public required Guid ChannelId { get; init; }

    /// <summary>
    /// S3's own identifier for the multipart upload. Required to presign parts,
    /// to complete, and to abort — without it the uploaded parts are
    /// unreachable and would bill until a lifecycle rule swept them.
    /// </summary>
    public required string S3UploadId { get; init; }

    public required string ObjectKey { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }

    public required long TotalBytes { get; init; }
    public required long PartSizeBytes { get; init; }
    public required int TotalParts { get; init; }

    // Metadata captured at initiation and replayed onto VideoUploaded when the
    // upload completes. Ingest does not own video metadata and Catalog cannot
    // read Ingest's store, so the event has to carry everything Catalog needs.
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string? CategoryId { get; init; }
    public string[] Tags { get; init; } = [];
    public string DefaultLanguage { get; init; } = "en";
    public VideoPrivacy Privacy { get; init; } = VideoPrivacy.Private;

    /// <summary>
    /// Part number to ETag, for every part S3 has confirmed.
    /// <para>
    /// This is what makes the upload resumable: on reconnect the client asks
    /// which parts already landed and re-sends only the gaps. It is also what
    /// completion needs — S3 validates the full set of part numbers and ETags.
    /// </para>
    /// </summary>
    public Dictionary<int, string> UploadedParts { get; init; } = [];

    public UploadStatus Status { get; set; } = UploadStatus.InProgress;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// DynamoDB TTL attribute, in epoch seconds. An abandoned upload disappears
    /// on its own; the matching S3 lifecycle rule aborts the orphaned multipart
    /// upload so incomplete parts stop accruing storage cost.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public long BytesUploaded => UploadedParts.Count == 0
        ? 0
        // Every part is PartSizeBytes except the last, which is whatever
        // remains. Assuming a full final part would report >100% complete.
        : UploadedParts.Keys.Sum(SizeOfPart);

    private long SizeOfPart(int partNumber) =>
        partNumber == TotalParts
            ? TotalBytes - (PartSizeBytes * (TotalParts - 1))
            : PartSizeBytes;

    public IReadOnlyList<int> MissingParts() =>
        Enumerable.Range(1, TotalParts)
            .Where(p => !UploadedParts.ContainsKey(p))
            .ToArray();

    public bool IsComplete => UploadedParts.Count == TotalParts;
}

public enum UploadStatus
{
    InProgress = 0,
    Completed = 1,
    Aborted = 2
}
