namespace JameX.Contracts.Events;

/// <summary>
/// Event type names. These are published as the SNS message attribute
/// <c>eventType</c>, which is what subscription filter policies match on — so a
/// consumer's queue only receives the event types it actually handles, rather
/// than every message on the topic being delivered and then discarded.
/// <para>
/// Changing one of these strings silently breaks every filter policy in
/// <c>infra/localstack/init</c>. Treat them as a published contract.
/// </para>
/// </summary>
public static class EventTypes
{
    public const string VideoUploaded = nameof(VideoUploaded);
    public const string VideoEncoded = nameof(VideoEncoded);
    public const string VideoEncodingFailed = nameof(VideoEncodingFailed);
    public const string VideoDeleted = nameof(VideoDeleted);
}

/// <summary>
/// Wrapper carried on every event. <see cref="EventId"/> is what makes
/// consumers idempotent: SNS-to-SQS is at-least-once, so a handler must be able
/// to recognise an event it has already applied.
/// </summary>
/// <param name="EventId">Stable unique id for this event instance.</param>
/// <param name="EventType">One of <see cref="EventTypes"/>.</param>
/// <param name="OccurredAt">When the producing service committed the change.</param>
/// <param name="Source">Producing service name, for tracing.</param>
/// <param name="Data">The event payload.</param>
public sealed record EventEnvelope<T>(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAt,
    string Source,
    T Data);

/// <summary>
/// Published by Ingest once every part of a multipart upload has landed and the
/// object exists in the raw bucket.
/// <para>
/// Consumers: Encoder (starts transcoding), Catalog (creates the metadata row).
/// </para>
/// <para>
/// The payload deliberately repeats the title, tags and privacy that the
/// uploader supplied rather than telling Catalog to "go and look it up".
/// Ingest does not own that data and Catalog cannot query Ingest's database —
/// so the event carries everything a consumer needs to act without a callback.
/// </para>
/// </summary>
public sealed record VideoUploaded(
    Guid VideoId,
    Guid ChannelId,
    Guid UploaderId,
    string Title,
    string? Description,
    string? CategoryId,
    string[] Tags,
    string DefaultLanguage,
    VideoPrivacy Privacy,
    string RawBucket,
    string RawObjectKey,
    long SizeBytes,
    string ContentType,
    DateTimeOffset UploadedAt);

/// <summary>
/// Published by Encoder when the ABR ladder and thumbnails are in blob storage.
/// <para>
/// Consumers: Catalog (flips status to Ready, records renditions), Search
/// (indexes the video — nothing unwatchable should be findable), Engagement
/// (initialises counters).
/// </para>
/// </summary>
public sealed record VideoEncoded(
    Guid VideoId,
    string MediaBucket,
    string MasterPlaylistKey,
    double DurationSeconds,
    EncodedRendition[] Renditions,
    EncodedThumbnail[] Thumbnails,
    string? PosterThumbnailKey,
    string EncoderProvider,
    double EncodingSeconds,
    DateTimeOffset EncodedAt);

/// <summary>One rung of the adaptive bitrate ladder.</summary>
public sealed record EncodedRendition(
    string Label,
    int Width,
    int Height,
    int BitrateKbps,
    string Codec,
    string PlaylistKey,
    long SizeBytes,
    int SegmentCount);

/// <summary>
/// A generated thumbnail. The image itself is an S3 object; only this reference
/// is stored in DynamoDB — the doc's stated Bigtable use case.
/// </summary>
public sealed record EncodedThumbnail(
    string ThumbnailId,
    string ObjectKey,
    int Width,
    int Height,
    double OffsetSeconds,
    bool IsPoster);

/// <summary>
/// Published by Encoder when a job exhausts its retry budget and is about to be
/// redriven to the DLQ. Lets Catalog surface a real failure to the uploader
/// instead of leaving the video stuck in Transcoding forever.
/// </summary>
public sealed record VideoEncodingFailed(
    Guid VideoId,
    string Reason,
    string Stage,
    int AttemptCount,
    DateTimeOffset FailedAt);

/// <summary>
/// Published by Catalog, the owner of video lifecycle. Every service holding
/// derived state about the video reacts: Search removes its index entries,
/// Engagement drops counters, and the objects are swept from blob storage.
/// </summary>
public sealed record VideoDeleted(
    Guid VideoId,
    Guid ChannelId,
    DateTimeOffset DeletedAt);
