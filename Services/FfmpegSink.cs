using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CupriCut.Services;

/// <summary>
/// One running ffmpeg, with a pipe to write frames into.
///
/// <para><b>Why it is its own thing.</b> Starting ffmpeg, holding its stderr, noticing that it died
/// before the pipe was full, and reading the finished file's pixel format were written three times
/// over - once for the sequential encode, once for the parallel one, once for the calibrator - and
/// the three had already begun to differ in what they reported when ffmpeg failed. They are one
/// thing now.</para>
///
/// <para>It also makes the interesting case cheap: <c>export</c> starts several of these and writes
/// the same frame into all of them, so N files cost one sweep instead of N.</para>
/// </summary>
internal sealed class FfmpegSink : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private FfmpegSink(Process process, string label, string outputPath)
    {
        _process = process;
        Label = label;
        OutputPath = outputPath;
    }

    /// <summary>What this sink is producing, for an error message that has to name one of several.</summary>
    public string Label { get; }

    public string OutputPath { get; }

    /// <summary>Where frames go. Raw RGBA, one frame after another, no framing of any kind.</summary>
    public Stream Input => _process.StandardInput.BaseStream;

    /// <summary>Start ffmpeg, or fail with an error that says what to do about it.</summary>
    public static FfmpegSink Start(string ffmpegPath, string args, string label, string outputPath, ILogger log)
    {
        log.LogInformation("ffmpeg {Args}", args);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var sink = new FfmpegSink(process, label, outputPath);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sink._stderr) sink._stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new CutPolicyException(
                $"Could not start ffmpeg at '{ffmpegPath}': {ex.Message}. " +
                "Set Cut:FfmpegPath, or call probe to see what this machine has. The Docker image carries ffmpeg; the release zips do not.");
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return sink;
    }

    /// <summary>Close the pipe, wait, and throw unless ffmpeg exited cleanly. Whether it actually
    /// wrote anything is the caller's to judge, because a target may be a numbered sequence rather
    /// than a single file. <paramref name="frames"/> is only for the message.</summary>
    public void Finish(int timeoutSeconds, int frames)
    {
        try { _process.StandardInput.Flush(); } catch (IOException) { /* it already died; the exit code says so */ }
        _process.StandardInput.Close();

        if (!_process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            Kill();
            throw new TimeoutException($"ffmpeg ({Label}) did not finish within Cut:FfmpegTimeoutSeconds ({timeoutSeconds}s). {Tail()}");
        }

        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg ({Label}) exited {_process.ExitCode} after {frames} frame(s). {Tail()}");
    }

    /// <summary>The failure to report when a WRITE blew up rather than the exit. A broken pipe means
    /// ffmpeg died first, and its stderr says why - which is far more use than "the pipe has been
    /// ended".</summary>
    public InvalidOperationException Broke(IOException cause, int frames)
    {
        Kill();
        return new InvalidOperationException($"ffmpeg ({Label}) stopped reading after {frames} frame(s). {Tail()}", cause);
    }

    public string Tail()
    {
        string text;
        lock (_stderr) text = _stderr.ToString().Trim();
        if (text.Length == 0) return "ffmpeg said nothing on stderr.";
        return "ffmpeg: " + string.Join(" | ", text.Split('\n').TakeLast(6).Select(l => l.Trim()));
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { /* it is already gone, which is what we wanted */ }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
