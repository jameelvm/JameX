namespace JameX.Contracts.Dtos;

/// <summary>
/// Opens an upload. The client sends only metadata — the bytes never pass
/// through the service. Chapter 2 sizes ingest at ~480 Gbps; routing that
/// through the application tier would make it the bottleneck and would need
/// disk or memory to buffer 600 MB originals. Instead the service issues
/// presigned URLs and the browser writes straight to S3.
/// </summary>
public sealed record CreateUploadRequest(
    /// <summary>
    /// The publishing channel. Supplied by the client rather than inferred,
    /// because a user may own several — but the *uploader* is never taken from
    /// the body; it comes from the authenticated caller.
    /// </summary>
    Guid ChannelId,
    string Title,
    string? Description,
    string? CategoryId,
    string[]? Tags,
    string? DefaultLanguage,
    VideoPrivacy Privacy,
    string FileName,
    string ContentType,
    long SizeBytes);

/// <summary>
/// The client is told how to slice the file. Part size is decided by the
/// service, not the client, because S3 constrains it: every part except the
/// last must be at least 5 MB, and there is a hard ceiling of 10,000 parts.
/// </summary>
public sealed record CreateUploadResponse(
    Guid UploadId,
    Guid VideoId,
    string ObjectKey,
    int PartSizeBytes,
    int TotalParts,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Presigned URLs are requested in batches rather than one at a time, so a
/// 600 MB upload does not need hundreds of round trips before it can start.
/// They are short-lived: a presigned URL is a bearer credential.
/// </summary>
public sealed record PresignPartsRequest(int[] PartNumbers);

public sealed record PresignedPart(
    int PartNumber,
    string Url,
    DateTimeOffset ExpiresAt);

public sealed record PresignPartsResponse(IReadOnlyList<PresignedPart> Parts);

/// <summary>
/// A part that landed in S3. The ETag is returned by S3 on the PUT and must be
/// echoed back at completion — S3 validates the whole set. This is why the raw
/// bucket's CORS config exposes the ETag header: without it the browser cannot
/// read its own upload response and the transfer can never be completed.
/// </summary>
public sealed record CompletedPart(int PartNumber, string ETag);

public sealed record CompleteUploadRequest(IReadOnlyList<CompletedPart> Parts);

public sealed record CompleteUploadResponse(
    Guid VideoId,
    VideoStatus Status,
    string RawObjectKey,
    long SizeBytes);

/// <summary>
/// Progress of an in-flight upload, used to resume after a dropped connection.
/// The client asks which parts already landed and re-sends only the gaps —
/// chapter 3's "server retains data temporarily to allow resumption".
/// </summary>
public sealed record UploadSessionStatus(
    Guid UploadId,
    Guid VideoId,
    string ObjectKey,
    int PartSizeBytes,
    int TotalParts,
    IReadOnlyList<int> UploadedPartNumbers,
    long BytesUploaded,
    long TotalBytes,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt)
{
    public double PercentComplete => TotalBytes == 0 ? 0 : (double)BytesUploaded / TotalBytes * 100d;
}
