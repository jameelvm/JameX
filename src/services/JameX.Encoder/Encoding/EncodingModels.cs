namespace JameX.Encoder.Encoding;

/// <summary>
/// What the source file turned out to be.
/// <para>
/// Probed before any encoding starts, because two decisions depend on it:
/// which ladder rungs are worth producing (never taller than the source), and
/// whether to emit an audio stream at all.
/// </para>
/// </summary>
public sealed record SourceProbe(
    double DurationSeconds,
    int Width,
    int Height,
    bool HasAudio,
    string? VideoCodec,
    string? AudioCodec);

/// <summary>A transcode request. Paths are local to the worker's scratch space.</summary>
public sealed record EncodingJob(
    Guid VideoId,
    string SourcePath,
    string OutputDirectory);

/// <summary>One finished rung, on local disk and ready to upload.</summary>
public sealed record EncodedRenditionFile(
    string Label,
    int Width,
    int Height,
    int BitrateKbps,
    string Codec,
    string PlaylistPath,
    string DirectoryPath,
    long SizeBytes,
    int SegmentCount);

public sealed record ThumbnailFile(
    string ThumbnailId,
    string Path,
    int Width,
    int Height,
    double OffsetSeconds,
    bool IsPoster);

/// <summary>Everything a completed job produced.</summary>
public sealed record EncodingResult(
    string Provider,
    string MasterPlaylistPath,
    double DurationSeconds,
    IReadOnlyList<EncodedRenditionFile> Renditions,
    IReadOnlyList<ThumbnailFile> Thumbnails,
    double EncodingSeconds);

/// <summary>
/// Thrown when a job fails in a way that identifies the stage, so
/// <c>VideoEncodingFailed</c> can tell the uploader <i>where</i> it broke
/// rather than just that it did.
/// </summary>
public sealed class EncodingFailedException(string stage, string reason, Exception? inner = null)
    : Exception(reason, inner)
{
    public string Stage { get; } = stage;
}
