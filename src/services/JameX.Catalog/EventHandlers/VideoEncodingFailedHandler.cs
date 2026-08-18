using JameX.Catalog.Caching;
using JameX.Catalog.Repositories;
using JameX.Contracts;
using JameX.Contracts.Events;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Messaging;

namespace JameX.Catalog.EventHandlers;

/// <summary>
/// Records a permanent encoding failure against the video.
/// <para>
/// Without this the uploader watches a spinner forever: the row would sit in
/// Transcoding with no explanation, and the only honest answer to "what
/// happened to my video?" would be "no idea". Encoder publishes this once it has
/// exhausted its retry budget and the job is on its way to the dead-letter
/// queue.
/// </para>
/// </summary>
public sealed class VideoEncodingFailedHandler(
    IVideoRepository videos,
    IInboxUnitOfWork inbox,
    IVideoCache cache,
    ILogger<VideoEncodingFailedHandler> logger)
    : EventHandlerBase<VideoEncodingFailed>(logger)
{
    public override string EventType => EventTypes.VideoEncodingFailed;

    protected override async Task HandleAsync(
        EventEnvelope<VideoEncodingFailed> envelope, CancellationToken ct)
    {
        var data = envelope.Data;

        inbox.ClaimEvent(envelope);

        var video = await videos.FindForUpdateAsync(data.VideoId, ct);

        if (video is null)
        {
            // Same ordering argument as VideoEncoded: retry until the upload
            // event has created the row.
            throw new InvalidOperationException(
                $"Video {data.VideoId} is not in the catalogue yet; retrying until VideoUploaded is applied.");
        }

        // A late failure must never demote a video that already succeeded. This
        // happens for real: the encoder retries, one attempt fails and publishes
        // this event while another attempt succeeds and publishes VideoEncoded,
        // and the two arrive in the wrong order. Ready wins.
        if (video.Status == VideoStatus.Ready)
        {
            Logger.LogWarning(
                "Ignoring encoding failure for {VideoId} — it is already Ready. Reason was: {Reason}",
                data.VideoId, data.Reason);

            // Still commit, so the inbox claim is recorded and the message is
            // not redelivered forever.
            await inbox.TrySaveAsync(ct);
            return;
        }

        video.Status = VideoStatus.Failed;
        video.FailureReason = data.Reason;
        video.FailureStage = data.Stage;
        video.AttemptCount = data.AttemptCount;
        video.UpdatedAt = DateTimeOffset.UtcNow;

        // PublishedAt is left alone. A failed video must not reach a feed, and
        // the partial index only holds rows with status = Ready anyway.

        if (await inbox.TrySaveAsync(ct))
        {
            // After the commit — same reasoning as VideoEncodedHandler.
            await cache.InvalidateAsync(data.VideoId, ct);

            Logger.LogWarning(
                "Video {VideoId} failed encoding at stage {Stage} after {Attempts} attempts: {Reason}",
                data.VideoId, data.Stage, data.AttemptCount, data.Reason);
        }
    }
}
