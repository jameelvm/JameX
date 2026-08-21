using JameX.Contracts;
using JameX.Contracts.Dtos;
using JameX.Contracts.Events;
using JameX.Ingest.Configuration;
using JameX.Ingest.Domain;
using JameX.Ingest.Repositories;
using JameX.Ingest.Storage;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Configuration;
using JameX.ServiceDefaults.Messaging;
using Microsoft.Extensions.Options;

namespace JameX.Ingest.Services;

/// <summary>
/// The upload lifecycle: open, hand out permission slips, track progress,
/// assemble, announce.
/// <para>
/// The shape of this service is dictated by one constraint from chapter 2 —
/// ingest runs at roughly 480 Gbps, so <b>video bytes must never pass through
/// this process</b>. Every method here deals in kilobytes of JSON: ids, part
/// numbers and ETags. The bytes go straight from the browser to S3.
/// </para>
/// </summary>
public interface IUploadService
{
    Task<OperationResult<CreateUploadResponse>> BeginAsync(
        Guid uploaderId, CreateUploadRequest request, CancellationToken ct);

    Task<OperationResult<PresignPartsResponse>> PresignAsync(
        Guid uploadId, Guid callerId, int[] partNumbers, CancellationToken ct);

    Task<OperationResult<UploadSessionStatus>> GetStatusAsync(
        Guid uploadId, Guid callerId, CancellationToken ct);

    Task<OperationResult<bool>> ReportPartAsync(
        Guid uploadId, Guid callerId, int partNumber, string eTag, CancellationToken ct);

    Task<OperationResult<CompleteUploadResponse>> CompleteAsync(
        Guid uploadId, Guid callerId, IReadOnlyList<CompletedPart> parts, CancellationToken ct);

    Task<OperationResult<bool>> AbortAsync(Guid uploadId, Guid callerId, CancellationToken ct);
}

internal sealed class UploadService(
    IUploadSessionRepository sessions,
    IRawUploadStore rawStore,
    IEventPublisher publisher,
    IOptions<UploadOptions> uploadOptions,
    IOptions<StorageOptions> storageOptions,
    IServiceIdentity identity,
    ILogger<UploadService> logger) : IUploadService
{
    private readonly UploadOptions _options = uploadOptions.Value;
    private readonly StorageOptions _storage = storageOptions.Value;

    public async Task<OperationResult<CreateUploadResponse>> BeginAsync(
        Guid uploaderId, CreateUploadRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim() ?? "";

        if (title.Length is < 1 or > 200)
            return OperationResult<CreateUploadResponse>.Invalid(
                "title", "Title must be between 1 and 200 characters.");

        if (request.SizeBytes is < 1)
            return OperationResult<CreateUploadResponse>.Invalid(
                "sizeBytes", "File size must be greater than zero.");

        // Rejected before a single byte moves. A client that discovers the limit
        // after uploading 20 GB has wasted its bandwidth and ours.
        if (request.SizeBytes > _options.MaxUploadBytes)
            return OperationResult<CreateUploadResponse>.Invalid(
                "sizeBytes",
                $"Maximum upload size is {_options.MaxUploadBytes / (1024 * 1024 * 1024)} GB.");

        if (!_options.AllowedContentTypes.Contains(request.ContentType, StringComparer.OrdinalIgnoreCase))
            return OperationResult<CreateUploadResponse>.Invalid(
                "contentType", $"Unsupported content type '{request.ContentType}'.");

        var (partSize, totalParts) =
            MultipartPlan.For(request.SizeBytes, _options.PreferredPartSizeBytes);

        // Minted here, before any bytes exist, so the client can poll for
        // progress and so the S3 key can embed it.
        var videoId = Guid.CreateVersion7();
        var objectKey = _storage.RawObjectKey(videoId, Path.GetExtension(request.FileName));

        // Open the S3 side first. If this fails there is no session to clean up;
        // if it succeeds but the session write fails, the TTL-driven lifecycle
        // rule aborts the orphaned upload.
        var s3UploadId = await rawStore.BeginAsync(objectKey, request.ContentType, ct);

        var session = new UploadSession
        {
            UploadId = Guid.CreateVersion7(),
            VideoId = videoId,
            UploaderId = uploaderId,
            ChannelId = request.ChannelId,
            S3UploadId = s3UploadId,
            ObjectKey = objectKey,
            FileName = request.FileName,
            ContentType = request.ContentType,
            TotalBytes = request.SizeBytes,
            PartSizeBytes = partSize,
            TotalParts = totalParts,
            Title = title,
            Description = request.Description,
            CategoryId = request.CategoryId,
            Tags = request.Tags ?? [],
            DefaultLanguage = string.IsNullOrWhiteSpace(request.DefaultLanguage)
                ? "en"
                : request.DefaultLanguage,
            Privacy = request.Privacy,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_options.SessionLifetime)
        };

        await sessions.CreateAsync(session, ct);

        logger.LogInformation(
            "Upload {UploadId} opened for video {VideoId}: {Bytes:N0} bytes in {Parts} parts of {PartSize:N0}",
            session.UploadId, videoId, request.SizeBytes, totalParts, partSize);

        return OperationResult<CreateUploadResponse>.Success(new CreateUploadResponse(
            session.UploadId, videoId, objectKey, (int)partSize, totalParts, session.ExpiresAt));
    }

    public async Task<OperationResult<PresignPartsResponse>> PresignAsync(
        Guid uploadId, Guid callerId, int[] partNumbers, CancellationToken ct)
    {
        var (session, failure) = await LoadAsync<PresignPartsResponse>(uploadId, callerId, ct);
        if (failure is not null) return failure;

        if (session!.Status != UploadStatus.InProgress)
            return OperationResult<PresignPartsResponse>.Conflict(
                $"This upload is {session.Status} and can no longer accept parts.");

        if (partNumbers.Length == 0)
            return OperationResult<PresignPartsResponse>.Invalid("partNumbers", "At least one part is required.");

        // Bounded so a single call cannot ask the service to sign ten thousand
        // URLs — signing is cheap, but not free, and this is unauthenticated
        // work an attacker could repeat.
        if (partNumbers.Length > _options.MaxPartsPerPresignRequest)
            return OperationResult<PresignPartsResponse>.Invalid(
                "partNumbers", $"At most {_options.MaxPartsPerPresignRequest} parts per request.");

        if (partNumbers.Any(p => p < 1 || p > session.TotalParts))
            return OperationResult<PresignPartsResponse>.Invalid(
                "partNumbers", $"Part numbers must be between 1 and {session.TotalParts}.");

        var expiresAt = DateTimeOffset.UtcNow.Add(_options.PresignedUrlLifetime);

        var signed = partNumbers
            .Distinct()
            .OrderBy(p => p)
            .Select(p => new PresignedPart(
                p, rawStore.PresignPart(session.ObjectKey, session.S3UploadId, p, expiresAt), expiresAt))
            .ToArray();

        return OperationResult<PresignPartsResponse>.Success(new PresignPartsResponse(signed));
    }

    /// <summary>
    /// The resume endpoint: which parts already landed, and how far along we are.
    /// </summary>
    public async Task<OperationResult<UploadSessionStatus>> GetStatusAsync(
        Guid uploadId, Guid callerId, CancellationToken ct)
    {
        var (session, failure) = await LoadAsync<UploadSessionStatus>(uploadId, callerId, ct);
        if (failure is not null) return failure;

        return OperationResult<UploadSessionStatus>.Success(new UploadSessionStatus(
            session!.UploadId,
            session.VideoId,
            session.ObjectKey,
            (int)session.PartSizeBytes,
            session.TotalParts,
            session.UploadedParts.Keys.Order().ToArray(),
            session.BytesUploaded,
            session.TotalBytes,
            session.CreatedAt,
            session.ExpiresAt));
    }

    public async Task<OperationResult<bool>> ReportPartAsync(
        Guid uploadId, Guid callerId, int partNumber, string eTag, CancellationToken ct)
    {
        var (loaded, failure) = await LoadAsync<bool>(uploadId, callerId, ct);
        if (failure is not null) return failure;

        var session = loaded!;

        if (partNumber < 1 || partNumber > session.TotalParts)
            return OperationResult<bool>.Invalid(
                "partNumber", $"Part number must be between 1 and {session.TotalParts}.");

        // S3 returns the ETag wrapped in quotes. Storing it inconsistently means
        // completion fails with a mismatch that is maddening to debug.
        var normalised = eTag.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(normalised))
            return OperationResult<bool>.Invalid("eTag", "An ETag is required.");

        await sessions.RecordPartAsync(uploadId, partNumber, normalised, ct);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// Assembles the object and announces the upload.
    /// </summary>
    public async Task<OperationResult<CompleteUploadResponse>> CompleteAsync(
        Guid uploadId, Guid callerId, IReadOnlyList<CompletedPart> parts, CancellationToken ct)
    {
        var (session, failure) = await LoadAsync<CompleteUploadResponse>(uploadId, callerId, ct);
        if (failure is not null) return failure;

        if (session!.Status == UploadStatus.Aborted)
            return OperationResult<CompleteUploadResponse>.Conflict("This upload was aborted.");

        // Prefer the client's list — it read those ETags straight from S3's
        // responses — but fall back to what we recorded, so a client that lost
        // its state can still finish.
        var effective = parts.Count > 0
            ? parts.Select(p => (p.PartNumber, ETag: p.ETag.Trim().Trim('"'))).ToArray()
            : session.UploadedParts.Select(kv => (PartNumber: kv.Key, ETag: kv.Value)).ToArray();

        if (effective.Length != session.TotalParts)
            return OperationResult<CompleteUploadResponse>.Invalid(
                "parts",
                $"Expected {session.TotalParts} parts but received {effective.Length}. " +
                $"Missing: {string.Join(", ", session.MissingParts())}");

        // Claim completion. The condition means only the first caller wins; a
        // retry gets false and reuses the id already stored, so the event it
        // republishes is byte-identical and every consumer's inbox rejects it
        // as a duplicate.
        var eventId = Guid.CreateVersion7();
        var isFirst = await sessions.TryMarkCompletedAsync(uploadId, eventId, ct);

        if (!isFirst)
        {
            var existing = await sessions.GetAsync(uploadId, ct);
            eventId = existing?.CompletionEventId ?? eventId;
            logger.LogInformation(
                "Upload {UploadId} was already completed; republishing event {EventId}", uploadId, eventId);
        }

        long sizeBytes;
        try
        {
            sizeBytes = await rawStore.CompleteAsync(
                session.ObjectKey, session.S3UploadId, effective, ct);
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchUpload")
        {
            // Already assembled by an earlier attempt — S3 discards the
            // multipart upload once it completes. Treat as success and carry on
            // to the publish, which is the step that may have been missed.
            logger.LogInformation("Multipart upload for {UploadId} was already completed in S3", uploadId);
            sizeBytes = session.TotalBytes;
        }

        await PublishUploadedAsync(session, eventId, sizeBytes, ct);

        return OperationResult<CompleteUploadResponse>.Success(new CompleteUploadResponse(
            session.VideoId, VideoStatus.Queued, session.ObjectKey, sizeBytes));
    }

    public async Task<OperationResult<bool>> AbortAsync(Guid uploadId, Guid callerId, CancellationToken ct)
    {
        var (session, failure) = await LoadAsync<bool>(uploadId, callerId, ct);
        if (failure is not null) return failure;

        if (session!.Status == UploadStatus.Completed)
            return OperationResult<bool>.Conflict("A completed upload cannot be aborted.");

        await rawStore.AbortAsync(session.ObjectKey, session.S3UploadId, ct);
        await sessions.SetStatusAsync(uploadId, UploadStatus.Aborted, ct);

        return OperationResult<bool>.Success(true);
    }

    /// <summary>
    /// Builds and publishes <c>VideoUploaded</c> with a caller-supplied event id.
    /// <para>
    /// The envelope is constructed by hand rather than via
    /// <c>PublishAsync</c> precisely so the id can be the stored one — the same
    /// mechanism the Catalog outbox relies on, for the same reason.
    /// </para>
    /// </summary>
    private async Task PublishUploadedAsync(
        UploadSession session, Guid eventId, long sizeBytes, CancellationToken ct)
    {
        var envelope = new EventEnvelope<VideoUploaded>(
            EventId: eventId,
            EventType: EventTypes.VideoUploaded,
            OccurredAt: DateTimeOffset.UtcNow,
            Source: identity.ServiceName,
            Data: new VideoUploaded(
                session.VideoId,
                session.ChannelId,
                session.UploaderId,
                session.Title,
                session.Description,
                session.CategoryId,
                session.Tags,
                session.DefaultLanguage,
                session.Privacy,
                _storage.RawBucket,
                session.ObjectKey,
                sizeBytes,
                session.ContentType,
                DateTimeOffset.UtcNow));

        await publisher.PublishEnvelopeAsync(
            EventTypes.VideoUploaded, JameXJson.Serialize(envelope), ct);

        logger.LogInformation(
            "Published VideoUploaded {EventId} for video {VideoId}", eventId, session.VideoId);
    }

    /// <summary>
    /// Loads a session and checks the caller owns it. 404 for absent, 403 for
    /// someone else's — never leak the existence of another user's upload.
    /// </summary>
    private async Task<(UploadSession? Session, OperationResult<T>? Failure)> LoadAsync<T>(
        Guid uploadId, Guid callerId, CancellationToken ct)
    {
        var session = await sessions.GetAsync(uploadId, ct);

        if (session is null)
            return (null, OperationResult<T>.NotFound());

        if (session.UploaderId != callerId)
            return (null, OperationResult<T>.Forbidden("This upload belongs to another user."));

        return (session, null);
    }
}
