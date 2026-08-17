using JameX.Contracts;

namespace JameX.Catalog.Domain;

/// <summary>
/// The system of record for what a video <i>is</i>. Chapter 2 puts this in the
/// eventually-consistent half of the design: a title edit that takes a second to
/// appear everywhere is fine, which is why this store can be tuned for read
/// volume rather than for the strict guarantees <c>jamex_users</c> needs.
/// <para>
/// The row is assembled from events over time, not written once. Ingest's
/// <c>VideoUploaded</c> creates it; Encoder's <c>VideoEncoded</c> fills in the
/// playback columns and flips the status. Until that second event arrives, the
/// playback half of this entity is legitimately null.
/// </para>
/// </summary>
public sealed class Video
{
    /// <summary>
    /// Assigned by Ingest and carried on the event — <b>not</b> generated here.
    /// <para>
    /// The uploader is handed an id before the bytes finish arriving so the
    /// client can poll for progress, and the raw S3 object key already embeds
    /// it. Catalog honours the id it is given; minting a second one would mean
    /// nothing could correlate the two.
    /// </para>
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// The owning channel, in <c>jamex_users</c> — a database this service
    /// cannot reach. So there is no foreign key here, and there cannot be.
    /// <para>
    /// The database can no longer refuse a video whose channel does not exist;
    /// the service has to. That is the real cost of decomposition, and it is
    /// paid here in exchange for Identity and Catalog scaling independently.
    /// </para>
    /// </summary>
    public required Guid ChannelId { get; init; }

    public required Guid UploaderId { get; init; }

    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? CategoryId { get; set; }

    /// <summary>
    /// A Postgres <c>text[]</c>, not a join table. Tags are read with the video
    /// on every request and never queried independently of it, so the join a
    /// separate table would force on every read buys nothing. A GIN index still
    /// makes "videos tagged X" a fast lookup.
    /// </summary>
    public string[] Tags { get; set; } = [];

    public string DefaultLanguage { get; set; } = "en";

    public VideoPrivacy Privacy { get; set; } = VideoPrivacy.Private;

    public VideoStatus Status { get; set; } = VideoStatus.Queued;

    // ---- upload facts, from VideoUploaded ------------------------------------

    public required string RawBucket { get; init; }
    public required string RawObjectKey { get; init; }
    public long SizeBytes { get; init; }
    public string? ContentType { get; init; }

    // ---- playback facts, from VideoEncoded -----------------------------------
    // All null until encoding succeeds. That is the point: a half-encoded video
    // has no master playlist, and the schema should not pretend otherwise.

    public string? MediaBucket { get; set; }
    public string? MasterPlaylistKey { get; set; }
    public double? DurationSeconds { get; set; }
    public string? PosterThumbnailKey { get; set; }
    public string? EncoderProvider { get; set; }
    public double? EncodingSeconds { get; set; }

    // ---- failure facts, from VideoEncodingFailed -----------------------------
    // Recorded so the uploader sees a real reason instead of a video stuck in
    // Transcoding forever.

    public string? FailureReason { get; set; }
    public string? FailureStage { get; set; }
    public int AttemptCount { get; set; }

    /// <summary>
    /// Chapter 5 splits the catalogue by popularity to decide how far towards
    /// the viewer content is pushed — only the hot head is worth edge storage.
    /// Nothing computes this yet; the column exists so the tiering job has
    /// somewhere to write when it arrives.
    /// </summary>
    public PopularityTier PopularityTier { get; set; } = PopularityTier.Cold;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Set when the video first becomes both <see cref="VideoStatus.Ready"/> and
    /// public. Distinct from <see cref="CreatedAt"/> because a video uploaded
    /// privately in January and published in March should sort by March.
    /// </summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public ICollection<Rendition> Renditions { get; } = [];
}
