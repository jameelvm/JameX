using JameX.Contracts.Dtos;
using JameX.Engagement.Repositories;
using JameX.ServiceDefaults.Application;

namespace JameX.Engagement.Services;

/// <summary>
/// Reads back the counters a video has accumulated, shaped as the
/// <see cref="EngagementCounts"/> the Gateway composes into the watch page.
/// </summary>
public interface IEngagementQueryService
{
    Task<OperationResult<EngagementCounts>> GetCountsAsync(Guid videoId, CancellationToken ct);
}

internal sealed class EngagementQueryService(
    IVideoCounterRepository counters, ICommentRepository comments) : IEngagementQueryService
{
    public async Task<OperationResult<EngagementCounts>> GetCountsAsync(Guid videoId, CancellationToken ct)
    {
        // Two independent stores, read in parallel — there is deliberately no
        // NotFound here even for a video with no rows at all yet: Engagement
        // does not own video existence, only Catalog does, and a video whose
        // VideoEncoded has not landed simply reads as zeros.
        var snapshotTask = counters.GetAsync(videoId, ct);
        var commentCountTask = comments.CountForVideoAsync(videoId, ct);

        await Task.WhenAll(snapshotTask, commentCountTask);

        var snapshot = snapshotTask.Result;

        return OperationResult<EngagementCounts>.Success(
            new EngagementCounts(snapshot.Views, snapshot.Likes, snapshot.Dislikes, commentCountTask.Result));
    }
}
