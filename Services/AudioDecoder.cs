using System.Diagnostics;
using System.Text;

namespace CupriCut.Services;

/// <summary>An audio file, decoded.</summary>
/// <param name="Samples">Mono, -1 to 1.</param>
/// <param name="SampleRate">What it was decoded at, which is what was asked for and not what the
/// file holds.</param>
/// <param name="Source">The path it came from.</param>
/// <param name="Sha256">A hash of the FILE, not of the samples. What catches the audio being
/// swapped underneath a project that was timed to it.</param>
public sealed record DecodedAudio(float[] Samples, int SampleRate, string Source, string Sha256)
{
    public double Seconds => SampleRate > 0 ? (double)Samples.Length / SampleRate : 0;
}

/// <summary>
/// ffmpeg, used as a decoder rather than an encoder.
///
/// <para>Everything about this is already a dependency: ffmpeg is required for video, it reads
/// every format anyone will point at this, and it will resample and downmix on the way out. Adding
/// an audio-decoding library to do a job the existing tool does is how a project acquires two
/// answers to one question.</para>
///
/// <para><b>Mono, at 22050 Hz, as 32-bit float.</b> Mono because a stereo image says nothing about
/// where a hit is. 22050 because onset and beat detection work on an envelope, not on timbre -
/// halving the rate halves the work and changes no answer this produces. Float because the
/// analysis wants -1 to 1 and converting from 16-bit integers is a step that can only introduce a
/// mistake.</para>
/// </summary>
public static class AudioDecoder
{
    /// <summary>What the analysis is fed. Not a setting: changing it changes the hop size in
    /// seconds and therefore every cue, so it is one number in one place.</summary>
    public const int Rate = 22050;

    /// <summary>Decode an audio file to mono samples.</summary>
    /// <param name="ffmpegPath">From <c>Cut:FfmpegPath</c>.</param>
    /// <param name="path">An absolute path, already resolved against the composition roots.</param>
    /// <param name="maxSeconds">A ceiling, so a mistaken argument cannot read a two-hour file into
    /// memory. At this rate a minute of audio is 5.3 MB.</param>
    public static DecodedAudio Decode(string ffmpegPath, string path, double maxSeconds = 1800)
    {
        if (!File.Exists(path))
            throw new CutPolicyException($"No audio file at '{path}'.");

        var args = string.Join(' ',
            "-v", "error",
            "-i", Quote(path),
            "-vn",                      // no cover art: an mp3's embedded JPEG is not audio
            "-ac", "1",
            "-ar", Rate.ToString(),
            "-f", "f32le",
            "-t", maxSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-");                       // to stdout

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CutPolicyException(
                $"Could not start ffmpeg at '{ffmpegPath}': {ex.Message}. "
                + "Set Cut:FfmpegPath, or call probe to see what this machine has.");
        }

        process.BeginErrorReadLine();

        // Read stdout to the end BEFORE waiting: ffmpeg blocks once the pipe buffer fills, so
        // waiting first deadlocks on anything longer than about a second.
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new CutPolicyException(
                $"ffmpeg could not decode '{Path.GetFileName(path)}' (exit {process.ExitCode}). "
                + Tail(stderr.ToString()));
        }

        var bytes = buffer.ToArray();
        if (bytes.Length < 4)
            throw new CutPolicyException($"'{Path.GetFileName(path)}' decoded to no audio at all. {Tail(stderr.ToString())}");

        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);

        return new DecodedAudio(samples, Rate, path, Hash(path));
    }

    /// <summary>Decode and listen, in one call.</summary>
    public static AudioAnalysis Analyse(string ffmpegPath, string path, double fps, double maxSeconds = 1800)
    {
        var decoded = Decode(ffmpegPath, path, maxSeconds);
        return AudioCues.Analyse(decoded.Samples, decoded.SampleRate, fps, Path.GetFileName(path));
    }

    /// <summary>Of the file's own bytes. A project stores this beside its cues, so that swapping
    /// the track underneath a composition timed to it is caught rather than discovered.</summary>
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    /// <summary>ffmpeg's last words, which are the ones that say what went wrong.</summary>
    private static string Tail(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : string.Join(" ", lines.TakeLast(3));
    }
}
