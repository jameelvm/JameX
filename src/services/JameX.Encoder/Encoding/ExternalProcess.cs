using System.Diagnostics;
using System.Text;

namespace JameX.Encoder.Encoding;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// FFmpeg's last few lines, which is where the actual error is. The full
    /// output is thousands of progress lines nobody wants in a log or an event.
    /// </summary>
    public string Tail(int lines = 6)
    {
        var source = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;

        return string.Join(" | ",
            source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .TakeLast(lines));
    }
}

/// <summary>
/// Runs a command-line tool and collects its output.
/// <para>
/// Three details here are the difference between working and mysteriously
/// hanging: both streams are drained concurrently, the process is killed on
/// timeout, and the whole tree is killed rather than just the parent.
/// </para>
/// </summary>
public static class ExternalProcess
{
    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // ArgumentList, never a concatenated string. The runtime escapes each
        // element, so a filename containing a space or a quote cannot break out
        // and become extra arguments.
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        // FFmpeg writes everything — progress and errors alike — to stderr.
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();

        // Both streams must be drained while the process runs. Waiting for exit
        // first deadlocks as soon as the output exceeds the pipe buffer, which
        // FFmpeg does within seconds.
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // entireProcessTree: FFmpeg can spawn children, and killing only the
            // parent leaves them holding the work directory and the CPU.
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

            throw new TimeoutException(
                $"{fileName} exceeded {timeout.TotalMinutes:F0} minutes and was terminated.");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
