using JameX.Contracts.Events;
using JameX.Encoder.Configuration;
using JameX.Encoder.Encoding;
using JameX.Encoder.Storage;
using JameX.ServiceDefaults.Configuration;
using JameX.ServiceDefaults.Messaging;
using Microsoft.Extensions.Options;

namespace JameX.Encoder.EventHandlers;

/// <summary>
/// The transcoding pipeline: download the original, produce the ladder, upload
/// it, and announce the result.
/// <para>
/// This is the only handler in the system that does real work measured in
/// minutes rather than milliseconds, which shapes three things: the consumer
/// extends the message's visibility while it runs, the scratch directory is
/// always cleaned up, and failures are split into <i>permanent</i> and
/// <i>transient</i> because retrying the first kind is pure waste.
/// </para>
/// </summary>
public sealed class VideoUploadedHandler(
    IEncodingJobRunner runner,
    IMediaStore mediaStore,
    IEventPublisher publisher,
    IEventDeduplicator deduplicator,
    IOptions<EncodingOptions> encodingOptions,
    IOptions<StorageOptions> storageOptions,
    ILogger<VideoUploadedHandler> logger)
    : EventHandlerBase<VideoUploaded>(logger)
{
    private readonly EncodingOptions _encoding = encodingOptions.Value;
    private readonly StorageOptions _storage = storageOptions.Value;

    public override string EventType => EventTypes.VideoUploaded;

    protected override async Task HandleAsync(EventEnvelope<VideoUploaded> envelope, CancellationToken ct)
    {
        var data = envelope.Data;

        // Encoder owns no relational store, so it cannot use the inbox pattern
        // Catalog uses — there is no transaction to enrol the claim in. Redis is
        // the best available filter here, and it is genuinely valuable because
        // re-encoding a video is minutes of CPU rather than a wasted UPDATE.
        // It narrows the window; it does not close it, which is why the rest of
        // this handler is written to be safe if it runs twice anyway.
        if (!await deduplicator.TryBeginAsync(envelope.EventId, ct))
        {
            Logger.LogInformation(
                "Skipping {EventId}: video {VideoId} is already being encoded", envelope.EventId, data.VideoId);
            return;
        }

        var workDirectory = Path.Combine(_encoding.WorkDirectory, data.VideoId.ToString("N"));
        var sourcePath = Path.Combine(workDirectory, "source" + Path.GetExtension(data.RawObjectKey));
        var outputDirectory = Path.Combine(workDirectory, "out");

        try
        {
            await mediaStore.DownloadAsync(data.RawBucket, data.RawObjectKey, sourcePath, ct);

            var result = await runner.RunAsync(
                new EncodingJob(data.VideoId, sourcePath, outputDirectory), ct);

            await UploadLadderAsync(data.VideoId, result, ct);
            await PublishEncodedAsync(data.VideoId, result, ct);
        }
        catch (EncodingFailedException ex)
        {
            // Permanent. The file is corrupt, has no video stream, or uses
            // something FFmpeg cannot read — and none of that improves on a
            // second attempt. Announce the failure and let the message be
            // deleted, rather than burning the retry budget to reach the same
            // conclusion three times and then sit in a DLQ.
            Logger.LogError(ex, "Encoding failed permanently for {VideoId} at {Stage}", data.VideoId, ex.Stage);
            await PublishFailedAsync(data.VideoId, ex.Stage, ex.Message, ct);
        }
        catch (TimeoutException ex)
        {
            // Also permanent in practice: a file that exceeds the job timeout
            // once will exceed it again.
            Logger.LogError(ex, "Encoding timed out for {VideoId}", data.VideoId);
            await PublishFailedAsync(data.VideoId, "timeout", ex.Message, ct);
        }
        // Everything else — S3 unreachable, disk full, SNS refusing — is
        // transient and deliberately left to propagate. The message is not
        // deleted, becomes visible again, and a later attempt may well succeed.
        finally
        {
            CleanUp(workDirectory);
        }
    }

    /// <summary>
    /// Uploads the ladder to the media bucket under the layout every other
    /// service expects from <see cref="StorageOptions"/>.
    /// </summary>
    private async Task UploadLadderAsync(Guid videoId, EncodingResult result, CancellationToken ct)
    {
        foreach (var rendition in result.Renditions)
        {
            await mediaStore.UploadDirectoryAsync(
                rendition.DirectoryPath, _storage.RenditionPrefix(videoId, rendition.Label).TrimEnd('/'), ct);
        }

        foreach (var thumbnail in result.Thumbnails)
        {
            await mediaStore.UploadFileAsync(
                thumbnail.Path, _storage.ThumbnailKey(videoId, thumbnail.ThumbnailId), ct);
        }

        // The master playlist goes LAST, deliberately. It is the entry point a
        // player fetches first, so publishing it before the segments exist would
        // create a window where the video looks ready and then fails to play.
        await mediaStore.UploadFileAsync(
            result.MasterPlaylistPath, _storage.MasterPlaylistKey(videoId), ct);
    }

    private async Task PublishEncodedAsync(Guid videoId, EncodingResult result, CancellationToken ct)
    {
        var renditions = result.Renditions
            .OrderBy(r => r.BitrateKbps)
            .Select(r => new EncodedRendition(
                r.Label, r.Width, r.Height, r.BitrateKbps, r.Codec,
                _storage.RenditionPlaylistKey(videoId, r.Label), r.SizeBytes, r.SegmentCount))
            .ToArray();

        var thumbnails = result.Thumbnails
            .Select(t => new EncodedThumbnail(
                t.ThumbnailId, _storage.ThumbnailKey(videoId, t.ThumbnailId),
                t.Width, t.Height, t.OffsetSeconds, t.IsPoster))
            .ToArray();

        await publisher.PublishAsync(EventTypes.VideoEncoded, new VideoEncoded(
            videoId,
            _storage.MediaBucket,
            _storage.MasterPlaylistKey(videoId),
            result.DurationSeconds,
            renditions,
            thumbnails,
            thumbnails.FirstOrDefault(t => t.IsPoster)?.ObjectKey,
            result.Provider,
            result.EncodingSeconds,
            DateTimeOffset.UtcNow), ct);

        Logger.LogInformation(
            "Published VideoEncoded for {VideoId}: {Rungs} rungs in {Seconds:F1}s",
            videoId, renditions.Length, result.EncodingSeconds);
    }

    private async Task PublishFailedAsync(Guid videoId, string stage, string reason, CancellationToken ct)
    {
        await publisher.PublishAsync(EventTypes.VideoEncodingFailed, new VideoEncodingFailed(
            videoId,
            reason.Length > 2000 ? reason[..2000] : reason,
            stage,
            // This path is only reached for failures no retry can fix, so the
            // budget was never spent. Transient faults never get here — they
            // propagate and are retried by the queue instead.
            AttemptCount: 1,
            DateTimeOffset.UtcNow), ct);
    }

    /// <summary>
    /// Always, on every path. A ladder is the source file plus every rung, so a
    /// handful of leaked jobs fills the scratch disk and every subsequent
    /// encode then fails for a reason that has nothing to do with the video.
    /// </summary>
    private void CleanUp(string workDirectory)
    {
        try
        {
            if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not clean up {WorkDirectory}", workDirectory);
        }
    }
}
