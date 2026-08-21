namespace JameX.Ingest.Configuration;

/// <summary>
/// Everything tunable about the upload feature, bound from the <c>Upload</c>
/// configuration section.
/// <para>
/// Owned by Ingest and defined inside Ingest, not in shared plumbing. The
/// ownership rule applies to configuration as much as to data: no other service
/// has any business knowing the name of the upload-session table or how long a
/// presigned URL lives. Shared options stay limited to genuinely cross-cutting
/// concerns — AWS endpoints, bucket names, the event topic.
/// </para>
/// <para>
/// Note what is <i>not</i> here: S3's 5 MB minimum part size and 10,000-part
/// ceiling. Those are limits AWS imposes, not choices we make, so they live as
/// constants in <see cref="Domain.MultipartPlan"/>. Configuration is for
/// decisions; constants are for facts.
/// </para>
/// </summary>
public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>DynamoDB table holding in-flight sessions.</summary>
    public string SessionsTableName { get; set; } = "jamex-upload-sessions";

    /// <summary>
    /// How long an unfinished upload may be resumed before its session expires
    /// via DynamoDB TTL.
    /// <para>
    /// A day is generous on purpose — chapter 3's whole point is that a user on
    /// a bad connection can come back and finish rather than restart. The
    /// matching S3 lifecycle rule aborts the orphaned multipart upload, so
    /// abandoned parts stop costing storage.
    /// </para>
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Lifetime of each presigned part URL.
    /// <para>
    /// Short, because <b>a presigned URL is a bearer credential</b> — anyone
    /// holding it can write those bytes. Long enough that a slow part on a poor
    /// connection still completes; the client re-requests URLs for anything
    /// that expires, which costs one cheap call.
    /// </para>
    /// </summary>
    public TimeSpan PresignedUrlLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Presigned URLs handed out per request. Batched so a 600 MB upload does
    /// not need hundreds of round trips before it can start, but bounded so one
    /// call cannot ask the service to sign ten thousand URLs.
    /// </summary>
    public int MaxPartsPerPresignRequest { get; set; } = 100;

    /// <summary>
    /// Largest upload accepted. The doc sizes a 5-minute video at ~600 MB raw;
    /// this allows well beyond that while refusing something absurd before it
    /// consumes any bandwidth.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024 * 1024;   // 20 GB

    /// <summary>
    /// Preferred slice size, subject to S3's constraints.
    /// <para>
    /// Larger than the 5 MB minimum deliberately: 600 MB at 5 MB parts is 120
    /// requests, each costing a round trip and a signature. 8 MB halves that
    /// without making a failed part expensive to retry — which is the real
    /// trade a part size makes.
    /// </para>
    /// </summary>
    public long PreferredPartSizeBytes { get; set; } = 8L * 1024 * 1024;   // 8 MB

    /// <summary>Accepted source types. Anything else is rejected at initiation.</summary>
    public string[] AllowedContentTypes { get; set; } =
        ["video/mp4", "video/quicktime", "video/x-matroska", "video/webm", "video/x-msvideo"];
}
