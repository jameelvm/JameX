using JameX.Contracts;
using JameX.Contracts.Events;
using JameX.Search.Repositories;
using JameX.ServiceDefaults.Messaging;

namespace JameX.Search.EventHandlers;

/// <summary>
/// Indexes a video the moment it becomes playable — nothing unwatchable
/// should be findable.
/// <para>
/// Search owns no relational store, so there is no inbox to make this
/// exactly-once — see <see cref="IEventDeduplicator"/>'s remarks, the same
/// Redis best-effort filter Encoder uses. What actually makes redelivery safe
/// here is that <c>IndexAsync</c> is a plain overwrite of deterministic
/// content: re-indexing the same title/description/tags twice produces the
/// exact same postings both times, so there is nothing for a duplicate to
/// corrupt.
/// </para>
/// </summary>
public sealed class VideoEncodedHandler(
    ISearchIndexRepository index,
    IEventDeduplicator deduplicator,
    ILogger<VideoEncodedHandler> logger)
    : EventHandlerBase<VideoEncoded>(logger)
{
    public override string EventType => EventTypes.VideoEncoded;

    protected override async Task HandleAsync(EventEnvelope<VideoEncoded> envelope, CancellationToken ct)
    {
        var data = envelope.Data;

        if (!await deduplicator.TryBeginAsync(envelope.EventId, ct))
        {
            Logger.LogInformation(
                "Skipping {EventId}: video {VideoId} is already indexed", envelope.EventId, data.VideoId);
            return;
        }

        await index.IndexAsync(data.VideoId, data.Title, data.Description, data.Tags, data.EncodedAt, ct);

        Logger.LogInformation("Indexed video {VideoId}: \"{Title}\"", data.VideoId, data.Title);
    }
}
