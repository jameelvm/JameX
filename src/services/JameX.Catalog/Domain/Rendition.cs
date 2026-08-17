namespace JameX.Catalog.Domain;

/// <summary>
/// One rung of the adaptive bitrate ladder — the 144p/360p/720p/1080p variants
/// Encoder produces from a single upload.
/// <para>
/// A separate table rather than a JSON column on the video, because the ladder
/// is a genuine one-to-many the application enumerates: the watch page lists
/// every rendition, and "how many videos have a 1080p rung?" is a question worth
/// being able to ask with an index rather than by parsing every document.
/// </para>
/// <para>
/// Unlike <see cref="Video.ChannelId"/> this <i>does</i> get a foreign key —
/// both tables live in <c>jamex_catalog</c>, owned by this service. The
/// difference between the two cases is the whole ownership rule in one schema.
/// </para>
/// </summary>
public sealed class Rendition
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VideoId { get; init; }

    /// <summary>Ladder rung name — <c>360p</c>, <c>720p</c>. Unique per video.</summary>
    public required string Label { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int BitrateKbps { get; init; }

    /// <summary>e.g. <c>h264</c>. Stored because a codec change is how a ladder gets re-cut.</summary>
    public required string Codec { get; init; }

    /// <summary>Media-bucket key of this rung's HLS playlist.</summary>
    public required string PlaylistKey { get; init; }

    public long SizeBytes { get; init; }
    public int SegmentCount { get; init; }

    public Video? Video { get; init; }
}
