namespace JameX.ServiceDefaults.Configuration;

/// <summary>
/// AWS endpoint configuration. Locally this points at LocalStack; in a real
/// account every value except <see cref="Region"/> is left unset and the SDK's
/// default credential and endpoint resolution takes over.
/// </summary>
public sealed class AwsOptions
{
    public const string SectionName = "Aws";

    /// <summary>
    /// Endpoint used for server-to-server calls. Empty in real AWS.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// Endpoint baked into URLs handed to a browser.
    /// <para>
    /// A presigned URL is signed including its host, so a URL signed for
    /// <c>http://localstack:4566</c> (reachable only inside the compose
    /// network) is useless to a browser on the host. Presigning therefore uses
    /// a second client bound to this address. In production the equivalent is
    /// making sure you sign for the same public domain the client will call.
    /// </para>
    /// </summary>
    public string? PublicServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>Static credentials for LocalStack. Never set these in production.</summary>
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    public bool UsesCustomEndpoint => !string.IsNullOrWhiteSpace(ServiceUrl);
}

/// <summary>
/// Object storage layout. Two buckets with opposite lifecycles: raw originals
/// are transient and enormous, encoded renditions are permanent and CDN-facing.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RawBucket { get; set; } = "jamex-raw";
    public string MediaBucket { get; set; } = "jamex-media";

    /// <summary>
    /// Base URL of the edge tier. Playback URLs point here rather than at the
    /// origin bucket so every segment request is cacheable at the PoP.
    /// </summary>
    public string CdnBaseUrl { get; set; } = "http://localhost:8081/media";

    public string RawObjectKey(Guid videoId, string extension) =>
        $"uploads/{videoId:N}/source{NormaliseExtension(extension)}";

    public string MasterPlaylistKey(Guid videoId) => $"videos/{videoId:N}/master.m3u8";

    public string RenditionPlaylistKey(Guid videoId, string label) =>
        $"videos/{videoId:N}/{label}/playlist.m3u8";

    public string RenditionPrefix(Guid videoId, string label) => $"videos/{videoId:N}/{label}/";

    public string ThumbnailKey(Guid videoId, string thumbnailId) =>
        $"videos/{videoId:N}/thumbs/{thumbnailId}.jpg";

    /// <summary>Turns a media-bucket object key into a client-facing CDN URL.</summary>
    public string ToCdnUrl(string objectKey) =>
        $"{CdnBaseUrl.TrimEnd('/')}/{objectKey.TrimStart('/')}";

    private static string NormaliseExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return ".bin";
        return extension.StartsWith('.') ? extension : "." + extension;
    }
}

/// <summary>
/// Messaging topology. One SNS topic carries every video lifecycle event;
/// each consuming service owns one SQS queue subscribed to it with a filter
/// policy, so a service only receives the event types it handles.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>Topic every service publishes video lifecycle events to.</summary>
    public string TopicName { get; set; } = "jamex-video-events";

    /// <summary>
    /// This service's own queue. Empty for services that only publish.
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>Messages to pull per receive call. SQS caps this at 10.</summary>
    public int MaxMessagesPerReceive { get; set; } = 10;

    /// <summary>
    /// Long-poll duration. 20s is the maximum and almost always correct: it
    /// removes empty-receive cost and cuts pickup latency to near zero.
    /// </summary>
    public int WaitTimeSeconds { get; set; } = 20;

    /// <summary>
    /// How long a handler may run before its message becomes visible again.
    /// Long jobs extend this while working rather than relying on one large
    /// value, because an over-long timeout delays recovery from a dead worker.
    /// </summary>
    public int VisibilityTimeoutSeconds { get; set; } = 300;
}
