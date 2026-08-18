using JameX.Catalog.Caching;
using JameX.Catalog.Domain;
using JameX.Catalog.Repositories;
using JameX.Contracts;
using JameX.Contracts.Events;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Messaging;

namespace JameX.Catalog.EventHandlers;

/// <summary>
/// Fills in the playback half of the row and makes the video watchable.
/// <para>
/// This is the moment a video becomes real: status flips to Ready, the master
/// playlist key is recorded, the ladder is written, and — if the uploader chose
/// public — <c>published_at</c> is stamped, which is what puts the video into
/// the public feed's partial index.
/// </para>
/// </summary>
public sealed class VideoEncodedHandler(
    IVideoRepository videos,
    IInboxUnitOfWork inbox,
    IVideoCache cache,
    ILogger<VideoEncodedHandler> logger)
    : EventHandlerBase<VideoEncoded>(logger)
{
    public override string EventType => EventTypes.VideoEncoded;

    protected override async Task HandleAsync(EventEnvelope<VideoEncoded> envelope, CancellationToken ct)
    {
        var data = envelope.Data;

        inbox.ClaimEvent(envelope);

        var video = await videos.FindForUpdateAsync(data.VideoId, ct);

        if (video is null)
        {
            // Ordering is not guaranteed. Both events reach this service on the
            // same queue, and the consumer processes a batch in parallel, so
            // VideoEncoded can genuinely be applied before VideoUploaded.
            //
            // Throwing is the correct response: the message is left undeleted,
            // becomes visible again after the visibility timeout, and by then
            // the upload event will have created the row. This is why ordering
            // problems on a queue are usually solved by retrying rather than by
            // trying to force order.
            throw new InvalidOperationException(
                $"Video {data.VideoId} is not in the catalogue yet; retrying until VideoUploaded is applied.");
        }

        video.MediaBucket = data.MediaBucket;
        video.MasterPlaylistKey = data.MasterPlaylistKey;
        video.DurationSeconds = data.DurationSeconds;
        video.PosterThumbnailKey = data.PosterThumbnailKey;
        video.EncoderProvider = data.EncoderProvider;
        video.EncodingSeconds = data.EncodingSeconds;

        video.Status = VideoStatus.Ready;

        // A previous failure is now stale — an encode that succeeded on retry
        // must not leave the old error text on the row.
        video.FailureReason = null;
        video.FailureStage = null;

        // Stamped once and never moved. A video unpublished and republished
        // keeps its original publication date, which is what makes feed
        // ordering stable.
        if (video.Privacy == VideoPrivacy.Public && video.PublishedAt is null)
            video.PublishedAt = data.EncodedAt;

        video.UpdatedAt = DateTimeOffset.UtcNow;

        await AddMissingRenditionsAsync(data, ct);

        if (await inbox.TrySaveAsync(ct))
        {
            // After the commit, never before. Invalidating first leaves a
            // window where a reader repopulates the cache from the old row and
            // the entry outlives the change it was meant to clear.
            await cache.InvalidateAsync(data.VideoId, ct);

            Logger.LogInformation(
                "Video {VideoId} is Ready — {Rungs} rungs, {Duration:F1}s, encoded by {Provider} in {Seconds:F1}s",
                data.VideoId, data.Renditions.Length, data.DurationSeconds,
                data.EncoderProvider, data.EncodingSeconds);
        }
    }

    /// <summary>
    /// Adds only the rungs not already recorded.
    /// <para>
    /// The unique index on <c>(video_id, label)</c> is the real guarantee — it
    /// would reject a duplicate 720p whatever this code did. Filtering here just
    /// avoids provoking a constraint violation on a legitimate partial replay,
    /// so the log stays free of errors that are not errors.
    /// </para>
    /// </summary>
    private async Task AddMissingRenditionsAsync(VideoEncoded data, CancellationToken ct)
    {
        var existing = await videos.GetRenditionLabelsAsync(data.VideoId, ct);

        var missing = data.Renditions
            .Where(r => !existing.Contains(r.Label))
            .Select(r => new Rendition
            {
                VideoId = data.VideoId,
                Label = r.Label,
                Width = r.Width,
                Height = r.Height,
                BitrateKbps = r.BitrateKbps,
                Codec = r.Codec,
                PlaylistKey = r.PlaylistKey,
                SizeBytes = r.SizeBytes,
                SegmentCount = r.SegmentCount
            })
            .ToArray();

        if (missing.Length > 0) videos.AddRenditions(missing);
    }
}
