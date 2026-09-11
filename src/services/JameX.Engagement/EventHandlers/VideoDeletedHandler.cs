using JameX.Contracts;
using JameX.Contracts.Events;
using JameX.Engagement.Repositories;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Messaging;

namespace JameX.Engagement.EventHandlers;

/// <summary>
/// Tears down every trace of a deleted video's engagement — counters and every
/// user's reaction to it. Comments are deliberately left out of this handler:
/// they are the one piece of engagement data with moderation value, so whether
/// to keep or purge them on delete is a decision for the comments module, not
/// a side effect of this one.
/// </summary>
public sealed class VideoDeletedHandler(
    IVideoCounterRepository counters,
    IUserReactionRepository reactions,
    IInboxUnitOfWork inbox,
    ILogger<VideoDeletedHandler> logger)
    : EventHandlerBase<VideoDeleted>(logger)
{
    public override string EventType => EventTypes.VideoDeleted;

    protected override async Task HandleAsync(EventEnvelope<VideoDeleted> envelope, CancellationToken ct)
    {
        var data = envelope.Data;

        // Same reasoning as VideoEncodedHandler: claim and commit first, since
        // the Dynamo deletes below cannot share that transaction. Both
        // DeleteAllAsync calls are naturally idempotent — deleting a row that
        // is already gone is a no-op — so a redelivery costs an extra Query
        // that finds nothing, never a wrong result.
        inbox.ClaimEvent(envelope);

        if (!await inbox.TrySaveAsync(ct))
            return;

        await Task.WhenAll(
            counters.DeleteAllAsync(data.VideoId, ct),
            reactions.DeleteAllForVideoAsync(data.VideoId, ct));

        Logger.LogInformation("Dropped counters and reactions for deleted video {VideoId}", data.VideoId);
    }
}
