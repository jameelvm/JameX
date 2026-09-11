using JameX.Contracts;
using JameX.Contracts.Events;
using JameX.Engagement.Repositories;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Messaging;

namespace JameX.Engagement.EventHandlers;

/// <summary>
/// Gives a newly playable video a determinate counter state.
/// <para>
/// Without this, the first <c>GetAsync</c> for a brand-new video would see zero
/// rows and have to decide whether that means "really zero" or "not
/// initialised yet" — indistinguishable states unless something makes them the
/// same thing on purpose. Writing likes = 0 and dislikes = 0 here removes the
/// ambiguity. View shards are deliberately left uncreated; see
/// <see cref="IVideoCounterRepository.RecordViewAsync"/>.
/// </para>
/// </summary>
public sealed class VideoEncodedHandler(
    IVideoCounterRepository counters,
    IInboxUnitOfWork inbox,
    ILogger<VideoEncodedHandler> logger)
    : EventHandlerBase<VideoEncoded>(logger)
{
    public override string EventType => EventTypes.VideoEncoded;

    protected override async Task HandleAsync(EventEnvelope<VideoEncoded> envelope, CancellationToken ct)
    {
        var data = envelope.Data;

        // The DynamoDB write below cannot join the inbox's Postgres transaction
        // — there is nothing to enrol it in. Claiming first and committing
        // before touching Dynamo narrows the redelivery window to "crashed
        // between the two writes"; it does not close it, same as Encoder's
        // Redis dedup. What closes it is InitializeAsync's own
        // attribute_not_exists condition, which makes it a genuine no-op on a
        // counter real traffic has already moved off zero.
        inbox.ClaimEvent(envelope);

        if (!await inbox.TrySaveAsync(ct))
            return;

        await counters.InitializeAsync(data.VideoId, ct);

        Logger.LogInformation("Initialised counters for video {VideoId}", data.VideoId);
    }
}
