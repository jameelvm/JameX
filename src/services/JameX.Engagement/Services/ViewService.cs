using JameX.Engagement.Repositories;

namespace JameX.Engagement.Services;

/// <summary>
/// Records a view. Deliberately thin — a real system would gate this behind a
/// minimum watch-time and a per-viewer cooldown to stop a refresh spam or a bot
/// from inflating a counter that has no uniqueness check at all (unlike
/// likes/dislikes, anyone can ping this endlessly). Kept out of scope here so
/// this module stays about the counter-scaling problem, not fraud detection;
/// tracked as a deferred item.
/// </summary>
public interface IViewService
{
    Task RecordViewAsync(Guid videoId, CancellationToken ct);
}

internal sealed class ViewService(IVideoCounterRepository counters) : IViewService
{
    public Task RecordViewAsync(Guid videoId, CancellationToken ct) =>
        counters.RecordViewAsync(videoId, ct);
}
