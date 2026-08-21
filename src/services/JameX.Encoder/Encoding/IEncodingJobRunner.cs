namespace JameX.Encoder.Encoding;

/// <summary>
/// Turns one source file into an adaptive bitrate ladder plus thumbnails.
/// <para>
/// The interface exists so the <i>strategy</i> can change without the pipeline
/// changing. FFmpeg in a container is right for a laptop and for small scale;
/// at the doc's volumes you would hand jobs to AWS Elemental MediaConvert
/// instead and stop operating an encoder fleet at all. Both satisfy this
/// contract, so the handler that consumes <c>VideoUploaded</c> never learns
/// which one it is talking to.
/// </para>
/// <para>
/// It is deliberately file-in, files-out. Nothing here knows about S3, SQS or
/// events — which is what makes it testable by pointing it at a file on disk.
/// </para>
/// </summary>
public interface IEncodingJobRunner
{
    /// <summary>Name recorded on the event, so the two providers stay comparable.</summary>
    string Provider { get; }

    /// <summary>Inspects the source without transcoding it.</summary>
    Task<SourceProbe> ProbeAsync(string sourcePath, CancellationToken ct);

    /// <summary>
    /// Produces the ladder, the master playlist and the thumbnails under
    /// <see cref="EncodingJob.OutputDirectory"/>.
    /// </summary>
    Task<EncodingResult> RunAsync(EncodingJob job, CancellationToken ct);
}
