using JameX.Contracts.Dtos;
using JameX.Gateway.Clients;
using JameX.ServiceDefaults.Application;
using JameX.ServiceDefaults.Hosting;

namespace JameX.Gateway.Services;

/// <summary>
/// The watch page, assembled from three services in one round trip from the
/// browser's point of view. This is the whole reason the Gateway exists as
/// more than a reverse proxy: Catalog knows the video, Engagement knows the
/// counts and the viewer's own reaction, Identity knows the channel's name,
/// and no one of them can see the other two's data.
/// </summary>
public interface IWatchAggregationService
{
    Task<OperationResult<VideoDetail>> GetWatchPageAsync(Guid videoId, CancellationToken ct);
}

internal sealed class WatchAggregationService(
    ICatalogReadClient catalog,
    IEngagementReadClient engagement,
    IIdentityReadClient identity,
    ICurrentUser currentUser,
    ILogger<WatchAggregationService> logger) : IWatchAggregationService
{
    public async Task<OperationResult<VideoDetail>> GetWatchPageAsync(Guid videoId, CancellationToken ct)
    {
        // Catalog first, and awaited alone: with no video there is nothing to
        // fan out for, and its channel id is what the Identity call needs.
        var video = await catalog.GetVideoAsync(videoId, ct);
        if (video is null) return OperationResult<VideoDetail>.NotFound();

        // The other two are independent of each other, so they run
        // concurrently rather than one after the other — the entire point of
        // doing this fan-out server-side, on a fast internal network, instead
        // of leaving the browser to make three round trips itself.
        var countsTask = engagement.GetCountsAsync(videoId, ct);
        var reactionTask = engagement.GetMyReactionAsync(videoId, currentUser.UserId, ct);
        var channelTask = identity.GetChannelAsync(video.ChannelId, ct);

        await Task.WhenAll(countsTask, reactionTask, channelTask);

        var merged = video with
        {
            ChannelName = channelTask.Result?.Name,
            // Falls back to Catalog's own zeroed EngagementCounts if
            // Engagement could not be reached — a watch page with counts
            // stuck at zero is a visible degradation, not a broken page.
            Counts = countsTask.Result ?? video.Counts,
            ViewerReaction = reactionTask.Result
        };

        logger.LogDebug("Assembled watch page for video {VideoId} from 3 services", videoId);

        return OperationResult<VideoDetail>.Success(merged);
    }
}
