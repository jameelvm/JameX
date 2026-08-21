using JameX.Encoder.Configuration;
using JameX.Encoder.Encoding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JameX.Encoder.Api;

/// <summary>
/// Development-only harness for the encoder.
/// <para>
/// Generates a synthetic source with FFmpeg's test-pattern generator, runs the
/// real ladder over it, and returns what came out. It exists because the
/// encoder is otherwise reachable only by publishing an event and waiting —
/// a slow and indirect way to find out that a scale filter is wrong.
/// </para>
/// <para>
/// Returns 404 outside Development, so it cannot be reached in a deployed
/// environment even if the route is somehow known.
/// </para>
/// </summary>
[ApiController]
[Route("debug")]
[Produces("application/json")]
public sealed class EncodingDebugController(
    IEncodingJobRunner runner,
    IOptions<EncodingOptions> options,
    IHostEnvironment environment,
    ILogger<EncodingDebugController> logger) : ControllerBase
{
    /// <summary>
    /// Encodes a generated test clip and reports the resulting ladder.
    /// </summary>
    /// <param name="seconds">Length of the synthetic source.</param>
    /// <param name="height">Source height, which decides how many rungs apply.</param>
    /// <param name="silent">Generate the source without an audio track.</param>
    [HttpPost("encode")]
    public async Task<IActionResult> Encode(
        CancellationToken ct,
        [FromQuery] int seconds = 10,
        [FromQuery] int height = 720,
        [FromQuery] bool silent = false)
    {
        if (!environment.IsDevelopment()) return NotFound();

        var settings = options.Value;
        var videoId = Guid.CreateVersion7();
        var workDirectory = Path.Combine(settings.WorkDirectory, $"debug-{videoId:N}");
        Directory.CreateDirectory(workDirectory);

        var sourcePath = Path.Combine(workDirectory, "source.mp4");

        try
        {
            await GenerateSourceAsync(settings, sourcePath, seconds, height, silent, ct);

            var probe = await runner.ProbeAsync(sourcePath, ct);

            var result = await runner.RunAsync(
                new EncodingJob(videoId, sourcePath, Path.Combine(workDirectory, "out")), ct);

            return Ok(new
            {
                source = new { probe.Width, probe.Height, probe.DurationSeconds, probe.HasAudio, probe.VideoCodec },
                result.Provider,
                result.DurationSeconds,
                encodingSeconds = Math.Round(result.EncodingSeconds, 2),
                masterPlaylist = System.IO.File.ReadAllText(result.MasterPlaylistPath),
                renditions = result.Renditions.Select(r => new
                {
                    r.Label, r.Width, r.Height, r.BitrateKbps, r.SegmentCount, r.SizeBytes
                }),
                thumbnails = result.Thumbnails.Select(t => new
                {
                    t.ThumbnailId, t.Width, t.Height, offsetSeconds = Math.Round(t.OffsetSeconds, 2), t.IsPoster
                })
            });
        }
        catch (EncodingFailedException ex)
        {
            logger.LogError(ex, "Debug encode failed at stage {Stage}", ex.Stage);
            return Problem(title: $"Encoding failed at {ex.Stage}", detail: ex.Message);
        }
        finally
        {
            // Scratch space is finite, and a debug run should not be the thing
            // that fills the disk.
            try { Directory.Delete(workDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Builds a synthetic clip with FFmpeg's built-in generators — a moving test
    /// pattern plus a sine tone. No sample file to ship, and the content is
    /// deterministic.
    /// </summary>
    private static async Task GenerateSourceAsync(
        EncodingOptions settings, string path, int seconds, int height, bool silent, CancellationToken ct)
    {
        var width = height * 16 / 9;
        if (width % 2 != 0) width++;

        var args = new List<string>
        {
            "-y",
            "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate=24:duration={seconds}"
        };

        if (!silent)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}"]);

        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);

        if (!silent) args.AddRange(["-c:a", "aac", "-shortest"]);

        args.Add(path);

        var result = await ExternalProcess.RunAsync(
            settings.FfmpegPath, args, TimeSpan.FromMinutes(5), ct);

        if (!result.Succeeded)
            throw new EncodingFailedException("generate-source", result.Tail());
    }
}
