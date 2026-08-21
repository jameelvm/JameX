using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using JameX.Encoder.Configuration;
using Microsoft.Extensions.Options;

namespace JameX.Encoder.Encoding;

/// <summary>
/// The FFmpeg implementation of the ladder: probe, encode each rung to HLS,
/// write a master playlist, extract thumbnails.
/// </summary>
public sealed class FfmpegEncodingJobRunner(
    IOptions<EncodingOptions> options,
    ILogger<FfmpegEncodingJobRunner> logger) : IEncodingJobRunner
{
    private readonly EncodingOptions _options = options.Value;

    public string Provider => "FFmpeg";

    public async Task<SourceProbe> ProbeAsync(string sourcePath, CancellationToken ct)
    {
        var result = await ExternalProcess.RunAsync(_options.FfprobePath,
        [
            "-v", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            sourcePath
        ], TimeSpan.FromMinutes(2), ct);

        if (!result.Succeeded)
            throw new EncodingFailedException("probe",
                $"ffprobe could not read the source: {result.Tail()}");

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        var streams = root.GetProperty("streams").EnumerateArray().ToArray();

        var video = streams.FirstOrDefault(s =>
            s.TryGetProperty("codec_type", out var t) && t.GetString() == "video");

        if (video.ValueKind == JsonValueKind.Undefined)
            throw new EncodingFailedException("probe", "The file contains no video stream.");

        var audio = streams.FirstOrDefault(s =>
            s.TryGetProperty("codec_type", out var t) && t.GetString() == "audio");

        // Duration lives on the container, but some formats omit it there and
        // carry it only on the stream — so fall back rather than reporting zero.
        var duration = ReadDouble(root.GetProperty("format"), "duration")
                       ?? ReadDouble(video, "duration")
                       ?? 0d;

        var probe = new SourceProbe(
            DurationSeconds: duration,
            Width: video.GetProperty("width").GetInt32(),
            Height: video.GetProperty("height").GetInt32(),
            HasAudio: audio.ValueKind != JsonValueKind.Undefined,
            VideoCodec: video.TryGetProperty("codec_name", out var vc) ? vc.GetString() : null,
            AudioCodec: audio.ValueKind != JsonValueKind.Undefined
                        && audio.TryGetProperty("codec_name", out var ac) ? ac.GetString() : null);

        logger.LogInformation(
            "Probed source: {Width}x{Height}, {Duration:F1}s, video={VideoCodec}, audio={AudioCodec}",
            probe.Width, probe.Height, probe.DurationSeconds, probe.VideoCodec, probe.AudioCodec ?? "none");

        return probe;
    }

    public async Task<EncodingResult> RunAsync(EncodingJob job, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        var probe = await ProbeAsync(job.SourcePath, ct);
        var rungs = SelectRungs(probe);

        Directory.CreateDirectory(job.OutputDirectory);

        var renditions = new List<EncodedRenditionFile>();
        foreach (var rung in rungs)
            renditions.Add(await EncodeRungAsync(job, probe, rung, ct));

        var masterPath = WriteMasterPlaylist(job.OutputDirectory, renditions);
        var thumbnails = await ExtractThumbnailsAsync(job, probe, ct);

        stopwatch.Stop();

        logger.LogInformation(
            "Encoded {VideoId}: {Rungs} rungs, {Thumbs} thumbnails, {Duration:F1}s of video in {Elapsed:F1}s",
            job.VideoId, renditions.Count, thumbnails.Count, probe.DurationSeconds,
            stopwatch.Elapsed.TotalSeconds);

        return new EncodingResult(
            Provider,
            masterPath,
            probe.DurationSeconds,
            renditions,
            thumbnails,
            stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Picks the rungs worth producing.
    /// <para>
    /// Never taller than the source. Upscaling a 480p phone clip to 1080p costs
    /// CPU and storage to produce a file that is <i>larger and blurrier</i> than
    /// the original — the extra pixels are invented. The lowest rung is always
    /// kept, so even a tiny source still yields something playable on a bad
    /// connection.
    /// </para>
    /// </summary>
    private LadderRung[] SelectRungs(SourceProbe probe)
    {
        var usable = _options.Ladder
            .Where(r => r.Height <= probe.Height)
            .OrderBy(r => r.Height)
            .ToArray();

        if (usable.Length == 0)
        {
            // The source is smaller than every rung. Produce one rung at the
            // source's own height rather than at the smallest rung's — encoding
            // a 180p clip as 240p would invent pixels, which is the upscaling
            // this method exists to avoid. The bitrate stays the low rung's,
            // because that is what a small frame needs.
            var smallest = _options.Ladder.MinBy(r => r.Height)!;
            var sourceHeight = probe.Height % 2 == 0 ? probe.Height : probe.Height - 1;

            logger.LogInformation(
                "Source is only {Height}p, below every rung; encoding a single {Height}p rung",
                probe.Height, sourceHeight);

            return
            [
                new LadderRung
                {
                    Label = $"{sourceHeight}p",
                    Height = sourceHeight,
                    VideoBitrateKbps = smallest.VideoBitrateKbps,
                    AudioBitrateKbps = smallest.AudioBitrateKbps,
                    MaxrateMultiplier = smallest.MaxrateMultiplier,
                    BufsizeMultiplier = smallest.BufsizeMultiplier
                }
            ];
        }

        logger.LogInformation(
            "Source is {Height}p; producing {Labels}",
            probe.Height, string.Join(", ", usable.Select(r => r.Label)));

        return usable;
    }

    private async Task<EncodedRenditionFile> EncodeRungAsync(
        EncodingJob job, SourceProbe probe, LadderRung rung, CancellationToken ct)
    {
        var directory = Path.Combine(job.OutputDirectory, rung.Label);
        Directory.CreateDirectory(directory);

        var playlistPath = Path.Combine(directory, "playlist.m3u8");
        var segmentPattern = Path.Combine(directory, "seg_%03d.ts");

        var width = EvenWidthFor(probe, rung.Height);
        var maxrate = (int)(rung.VideoBitrateKbps * rung.MaxrateMultiplier);
        var bufsize = (int)(rung.VideoBitrateKbps * rung.BufsizeMultiplier);

        // Two keyframes per segment interval, assumed 24fps. Fixed GOP length is
        // the point: see the comment on -sc_threshold below.
        var gop = _options.SegmentSeconds * 24;

        var args = new List<string>
        {
            "-y",
            "-i", job.SourcePath,

            // -2 keeps the aspect ratio and rounds to an even number, which
            // H.264 requires. Fixing both dimensions would letterbox a vertical
            // phone video into a landscape frame.
            "-vf", $"scale=-2:{rung.Height}",

            "-c:v", "libx264",
            "-profile:v", "main",
            "-preset", "veryfast",

            // The three flags that make adaptive switching actually work.
            // A player can only change quality at a segment boundary, and only
            // if every rung's boundaries fall at the same instant. Fixed GOP
            // length (-g/-keyint_min) plus disabled scene-change keyframes
            // (-sc_threshold 0) forces exactly that alignment. Without them,
            // FFmpeg inserts keyframes wherever the content changes, segments
            // drift apart between rungs, and switching quality produces a
            // visible stutter or an outright gap.
            "-g", gop.ToString(),
            "-keyint_min", gop.ToString(),
            "-sc_threshold", "0",

            "-b:v", $"{rung.VideoBitrateKbps}k",
            "-maxrate", $"{maxrate}k",
            "-bufsize", $"{bufsize}k"
        };

        if (probe.HasAudio)
        {
            args.AddRange([
                "-c:a", "aac",
                "-b:a", $"{rung.AudioBitrateKbps}k",
                "-ac", "2"
            ]);
        }
        else
        {
            // A silent video is legitimate. Asking for an audio stream that does
            // not exist fails the whole encode.
            args.Add("-an");
        }

        args.AddRange([
            "-f", "hls",
            "-hls_time", _options.SegmentSeconds.ToString(),
            "-hls_playlist_type", "vod",
            "-hls_segment_filename", segmentPattern,
            playlistPath
        ]);

        var result = await ExternalProcess.RunAsync(
            _options.FfmpegPath, args, _options.JobTimeout, ct);

        if (!result.Succeeded)
            throw new EncodingFailedException($"encode:{rung.Label}",
                $"ffmpeg failed for {rung.Label}: {result.Tail()}");

        var segments = Directory.GetFiles(directory, "seg_*.ts");
        var sizeBytes = segments.Sum(f => new FileInfo(f).Length)
                        + new FileInfo(playlistPath).Length;

        logger.LogInformation(
            "  {Label}: {Width}x{Height}, {Segments} segments, {Size:N0} bytes",
            rung.Label, width, rung.Height, segments.Length, sizeBytes);

        return new EncodedRenditionFile(
            rung.Label, width, rung.Height, rung.VideoBitrateKbps, "h264",
            playlistPath, directory, sizeBytes, segments.Length);
    }

    /// <summary>
    /// Writes the master playlist by hand rather than using FFmpeg's
    /// <c>var_stream_map</c>, because it is a handful of lines and being able to
    /// read exactly what the player is offered is worth more than the brevity.
    /// <para>
    /// This file is what makes the stream adaptive: the player fetches it first,
    /// sees every rung with its bandwidth and resolution, and chooses — then
    /// re-chooses mid-playback as conditions change.
    /// </para>
    /// </summary>
    private static string WriteMasterPlaylist(
        string outputDirectory, IReadOnlyList<EncodedRenditionFile> renditions)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#EXTM3U");
        builder.AppendLine("#EXT-X-VERSION:3");

        // Lowest first: a player with no bandwidth estimate yet starts at the
        // top of this list, and starting low means playback begins quickly and
        // then improves, rather than stalling on a stream too big for the link.
        foreach (var rendition in renditions.OrderBy(r => r.BitrateKbps))
        {
            // BANDWIDTH is peak, not average, and must include audio — a player
            // that believes a stream is cheaper than it is will pick it and then
            // rebuffer.
            var bandwidth = (int)(rendition.BitrateKbps * 1.1 * 1000);

            builder.AppendLine(
                $"#EXT-X-STREAM-INF:BANDWIDTH={bandwidth}," +
                $"RESOLUTION={rendition.Width}x{rendition.Height}," +
                $"CODECS=\"avc1.4d401f,mp4a.40.2\"");

            // Relative path, so the playlist works unchanged behind any CDN
            // hostname.
            builder.AppendLine($"{rendition.Label}/playlist.m3u8");
        }

        var path = Path.Combine(outputDirectory, "master.m3u8");
        File.WriteAllText(path, builder.ToString());
        return path;
    }

    /// <summary>
    /// Pulls still frames spread across the video.
    /// <para>
    /// Offsets skip the very start and end, where videos tend to be black
    /// frames or titles. The middle one becomes the poster — the image shown
    /// before playback starts.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ThumbnailFile>> ExtractThumbnailsAsync(
        EncodingJob job, SourceProbe probe, CancellationToken ct)
    {
        if (probe.DurationSeconds <= 0) return [];

        var directory = Path.Combine(job.OutputDirectory, "thumbs");
        Directory.CreateDirectory(directory);

        var count = Math.Max(1, _options.ThumbnailCount);
        var posterIndex = count / 2;
        var thumbnails = new List<ThumbnailFile>(count);

        for (var i = 0; i < count; i++)
        {
            var fraction = (i + 1) / (double)(count + 1);
            var offset = probe.DurationSeconds * fraction;
            var id = $"t{i + 1}";
            var path = Path.Combine(directory, $"{id}.jpg");

            var result = await ExternalProcess.RunAsync(_options.FfmpegPath,
            [
                "-y",
                // -ss BEFORE -i seeks by jumping straight to the keyframe rather
                // than decoding from the start. On a long video that is the
                // difference between milliseconds and minutes.
                "-ss", offset.ToString("F3", CultureInfo.InvariantCulture),
                "-i", job.SourcePath,
                "-frames:v", "1",
                "-vf", $"scale={_options.ThumbnailWidth}:-2",
                "-q:v", "3",
                path
            ], TimeSpan.FromMinutes(2), ct);

            if (!result.Succeeded || !File.Exists(path))
            {
                // A missing thumbnail is cosmetic. Failing the whole encode for
                // it would throw away a perfectly good ladder.
                logger.LogWarning(
                    "Thumbnail {Id} at {Offset:F1}s failed: {Error}", id, offset, result.Tail(2));
                continue;
            }

            var height = (int)Math.Round(
                _options.ThumbnailWidth * (double)probe.Height / probe.Width / 2) * 2;

            thumbnails.Add(new ThumbnailFile(
                id, path, _options.ThumbnailWidth, height, offset, IsPoster: i == posterIndex));
        }

        return thumbnails;
    }

    /// <summary>
    /// The width <c>scale=-2:h</c> will produce, so the master playlist reports
    /// the real resolution rather than the ladder's nominal one.
    /// </summary>
    private static int EvenWidthFor(SourceProbe probe, int targetHeight)
    {
        if (probe.Height == 0) return 0;
        var width = (int)Math.Round(probe.Width * (double)targetHeight / probe.Height);
        return width % 2 == 0 ? width : width + 1;
    }

    private static double? ReadDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
