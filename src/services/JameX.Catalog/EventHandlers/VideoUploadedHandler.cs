using JameX.Catalog.Domain;
using JameX.Catalog.Repositories;
using JameX.Contracts;
using JameX.Contracts.Events;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Messaging;

namespace JameX.Catalog.EventHandlers;

/// <summary>
/// Creates the metadata row when Ingest reports a completed upload.
/// <para>
/// This is the first of three events that build a video row. It writes what is
/// known at upload time and leaves every playback column null — nobody has
/// opened the file yet, so its duration and renditions are genuinely unknown.
/// </para>
/// </summary>
public sealed class VideoUploadedHandler(
    IVideoRepository videos,
    IInboxUnitOfWork inbox,
    ILogger<VideoUploadedHandler> logger)
    : EventHandlerBase<VideoUploaded>(logger)
{
    public override string EventType => EventTypes.VideoUploaded;

    protected override async Task HandleAsync(EventEnvelope<VideoUploaded> envelope, CancellationToken ct)
    {
        var data = envelope.Data;

        // Staged first, committed last, together with the row below.
        inbox.ClaimEvent(envelope);

        // Belt and braces. The inbox claim already blocks a redelivery, but a
        // video could also arrive from a replayed topic under a fresh event id,
        // and re-inserting the same primary key would throw instead of being
        // recognised as a no-op.
        if (await videos.ExistsAsync(data.VideoId, ct))
        {
            Logger.LogInformation(
                "Video {VideoId} already exists; treating {EventId} as a replay",
                data.VideoId, envelope.EventId);
            return;
        }

        videos.Add(new Video
        {
            // Ingest's id, used verbatim — the raw S3 object is already named
            // after it, so minting a new one would orphan the file.
            Id = data.VideoId,
            ChannelId = data.ChannelId,
            UploaderId = data.UploaderId,

            Title = data.Title,
            Description = data.Description,
            CategoryId = data.CategoryId,
            Tags = data.Tags,
            DefaultLanguage = data.DefaultLanguage,
            Privacy = data.Privacy,

            // Catalog decides this, not the event: the bytes have landed and an
            // encode is now owed. Ingest does not get to declare our status.
            Status = VideoStatus.Queued,

            RawBucket = data.RawBucket,
            RawObjectKey = data.RawObjectKey,
            SizeBytes = data.SizeBytes,
            ContentType = data.ContentType,

            CreatedAt = data.UploadedAt,
            UpdatedAt = DateTimeOffset.UtcNow

            // PublishedAt stays null even for a public video. "Public" is the
            // uploader's intent; "published" means a viewer can press play, and
            // they cannot until the encode lands.
        });

        if (await inbox.TrySaveAsync(ct))
            Logger.LogInformation("Created video {VideoId} as Queued", data.VideoId);
    }
}
