using JameX.Contracts;
using JameX.Contracts.Events;
using JameX.Search.Repositories;
using JameX.ServiceDefaults.Messaging;

namespace JameX.Search.EventHandlers;

/// <summary>
/// Removes every posting for a deleted video. Idempotent by construction —
/// deleting rows that are already gone is a no-op — so this needs no
/// deduplication at all, unlike <see cref="VideoEncodedHandler"/>.
/// </summary>
public sealed class VideoDeletedHandler(
    ISearchIndexRepository index, ILogger<VideoDeletedHandler> logger)
    : EventHandlerBase<VideoDeleted>(logger)
{
    public override string EventType => EventTypes.VideoDeleted;

    protected override async Task HandleAsync(EventEnvelope<VideoDeleted> envelope, CancellationToken ct)
    {
        await index.DeleteAllForVideoAsync(envelope.Data.VideoId, ct);

        Logger.LogInformation("Removed video {VideoId} from the search index", envelope.Data.VideoId);
    }
}
