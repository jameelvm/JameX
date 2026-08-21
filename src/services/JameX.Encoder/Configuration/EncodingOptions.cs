namespace JameX.Encoder.Configuration;

/// <summary>
/// Everything tunable about transcoding, bound from the <c>Encoding</c> section
/// and owned by the Encoder service alone.
/// </summary>
public sealed class EncodingOptions
{
    public const string SectionName = "Encoding";

    /// <summary>
    /// Which <c>IEncodingJobRunner</c> to use. FFmpeg locally; a MediaConvert
    /// adapter would slot in behind the same interface without any caller
    /// change — which is the whole reason the interface exists.
    /// </summary>
    public string Provider { get; set; } = "FFmpeg";

    /// <summary>
    /// Scratch space for the raw download and the encoded ladder.
    /// <para>
    /// Transcoding is heavily disk-bound: this directory holds the full source
    /// file <i>plus</i> every rung it produces, so a 600 MB upload can need
    /// several gigabytes here. A real deployment mounts fast ephemeral storage.
    /// </para>
    /// </summary>
    public string WorkDirectory { get; set; } = "/var/jamex/work";

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// HLS segment length.
    /// <para>
    /// The central trade in adaptive streaming. Short segments let the player
    /// react to a bandwidth drop sooner, because it can only switch quality at
    /// a segment boundary — but each one costs an HTTP request and its own
    /// overhead. Six seconds is the common compromise and matches Apple's
    /// recommendation.
    /// </para>
    /// </summary>
    public int SegmentSeconds { get; set; } = 6;

    /// <summary>
    /// The adaptive bitrate ladder, lowest rung first.
    /// <para>
    /// Rungs taller than the source are skipped at run time — upscaling burns
    /// CPU and bytes to produce something that looks <i>worse</i> than the
    /// original, never better.
    /// </para>
    /// </summary>
    public LadderRung[] Ladder { get; set; } =
    [
        new() { Label = "240p",  Height = 240,  VideoBitrateKbps = 400,  AudioBitrateKbps = 64 },
        new() { Label = "360p",  Height = 360,  VideoBitrateKbps = 800,  AudioBitrateKbps = 96 },
        new() { Label = "480p",  Height = 480,  VideoBitrateKbps = 1400, AudioBitrateKbps = 128 },
        new() { Label = "720p",  Height = 720,  VideoBitrateKbps = 2800, AudioBitrateKbps = 128 },
        new() { Label = "1080p", Height = 1080, VideoBitrateKbps = 5000, AudioBitrateKbps = 192 }
    ];

    /// <summary>How many thumbnails to extract, spread across the video.</summary>
    public int ThumbnailCount { get; set; } = 3;

    public int ThumbnailWidth { get; set; } = 640;

    /// <summary>
    /// Ceiling on a single transcode. A job that exceeds it is killed and the
    /// message retried — without this, one pathological file can occupy a
    /// worker indefinitely while the queue backs up behind it.
    /// </summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>One rung of the ladder.</summary>
public sealed class LadderRung
{
    public required string Label { get; set; }

    /// <summary>
    /// Target height. Width is derived from the source's aspect ratio rather
    /// than fixed, so a vertical phone video is not letterboxed into a
    /// landscape frame.
    /// </summary>
    public required int Height { get; set; }

    public required int VideoBitrateKbps { get; set; }
    public required int AudioBitrateKbps { get; set; }

    /// <summary>
    /// Peak bitrate allowed, as a multiple of the target. Some headroom absorbs
    /// complex scenes without the encoder smearing them, while still keeping
    /// the stream within what the rung promises the player.
    /// </summary>
    public double MaxrateMultiplier { get; set; } = 1.07;

    /// <summary>Decoder buffer, as a multiple of the target bitrate.</summary>
    public double BufsizeMultiplier { get; set; } = 1.5;
}
