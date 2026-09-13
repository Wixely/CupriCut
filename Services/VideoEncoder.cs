using System.Diagnostics;
using System.Text;
using CupriCut.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CupriCut.Services;

/// <summary>What ffmpeg is on this machine, if anything.</summary>
public sealed record FfmpegInfo(bool Available, string Path, string? Version, IReadOnlyList<string> Encoders, string? Error);

/// <summary>The result of an encode.</summary>
public sealed record VideoResult(string Path, long Bytes, int Frames, double Fps, double Seconds, string Codec, bool Alpha);

/// <summary>
/// Raw RGBA on ffmpeg's stdin.
///
/// <para>ffmpeg never enters the engine and it does not enter the sweep either: the sweep produces
/// premultiplied RGBA in a surface it already owns, and each frame goes straight down the pipe. No
/// PNG is encoded on this path, which is why 90 frames take 0.74 s rather than the four seconds a
/// PNG sequence of the same length costs.</para>
///
/// <para>Alpha needs both halves to agree: a transparent clear on the render side, and a codec that
/// carries an alpha channel on this one. h264 does not, so asking for alpha selects a codec that
/// does rather than silently producing a black background.</para>
/// </summary>
public sealed class VideoEncoder(CupriCutService cut, ILogger<VideoEncoder> log)
{
    /// <summary>Codecs offered by name, and what they mean to ffmpeg. Keyed the way an agent would
    /// ask.</summary>
    public static readonly IReadOnlyDictionary<string, VideoCodec> Codecs = new Dictionary<string, VideoCodec>(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = new("h264", "libx264", "yuv420p", ".mp4", Alpha: false, ["-preset", "medium", "-crf", "18", "-movflags", "+faststart"]),
        ["h265"] = new("h265", "libx265", "yuv420p", ".mp4", Alpha: false, ["-preset", "medium", "-crf", "22", "-tag:v", "hvc1", "-movflags", "+faststart"]),
        ["vp9"] = new("vp9", "libvpx-vp9", "yuva420p", ".webm", Alpha: true, ["-b:v", "0", "-crf", "30"]),
        ["prores"] = new("prores", "prores_ks", "yuva444p10le", ".mov", Alpha: true, ["-profile:v", "4444"]),
        ["gif"] = new("gif", "gif", "rgb8", ".gif", Alpha: false, []),
    };

    /// <summary>The codec picked when the caller names none: h264 normally, and the first
    /// alpha-capable one when the caller asked for alpha.</summary>
    public static VideoCodec Default(bool alpha) => alpha ? Codecs["vp9"] : Codecs["h264"];

    public static VideoCodec Resolve(string? name, bool alpha)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default(alpha);
        if (!Codecs.TryGetValue(name, out var codec))
            throw new ArgumentException($"Unknown codec '{name}'. Known codecs: {string.Join(", ", Codecs.Keys)}.", nameof(name));
        if (alpha && !codec.Alpha)
            throw new ArgumentException(
                $"Codec '{codec.Name}' carries no alpha channel, so alpha:true would produce an opaque video rather than a transparent one. " +
                $"Use {string.Join(" or ", Codecs.Values.Where(c => c.Alpha).Select(c => c.Name))}.", nameof(name));
        return codec;
    }

    /// <summary>Is ffmpeg there, and what can it encode?</summary>
    public FfmpegInfo Probe()
    {
        var path = cut.Options.FfmpegPath;
        try
        {
            var version = Run(path, "-hide_banner -version", TimeSpan.FromSeconds(10));
            var firstLine = version.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

            var encoders = Run(path, "-hide_banner -encoders", TimeSpan.FromSeconds(10));
            var present = Codecs.Values
                .Where(c => encoders.Contains(" " + c.Encoder, StringComparison.Ordinal))
                .Select(c => c.Name)
                .ToList();

            return new FfmpegInfo(true, path, firstLine, present, null);
        }
        catch (Exception ex)
        {
            return new FfmpegInfo(false, path, null, [], ex.Message);
        }
    }

    /// <summary>Sweep the composition and pipe every kept frame into ffmpeg.</summary>
    public (VideoResult Video, SweepReport Sweep) Encode(SweepSpec spec, string outputPath, VideoCodec codec, double outputFps) =>
        Encode(cut.LoadComposition(spec.Composition), spec, outputPath, codec, outputFps);

    /// <summary>The same encode over a composition already in hand, so a project's render defaults
    /// are read once rather than by loading it twice.</summary>
    public (VideoResult Video, SweepReport Sweep) Encode(Composition composition, SweepSpec spec, string outputPath, VideoCodec codec, double outputFps)
    {
        cut.EnsureVideoAllowed();

        // Resolved the same way the sweep resolves them - caller, then project, then configuration
        // - because the pipe has to be told the exact frame size the sweep will produce.
        var defaults = composition.Defaults;
        var scale = spec.Scale > 0 ? spec.Scale : defaults?.Scale > 0 ? defaults.Scale : 1;
        var alpha = spec.Alpha ?? defaults?.Alpha ?? false;
        var width = (spec.Width > 0 ? spec.Width : defaults?.Width > 0 ? defaults.Width : cut.Options.DefaultWidth) * scale;
        var height = (spec.Height > 0 ? spec.Height : defaults?.Height > 0 ? defaults.Height : cut.Options.DefaultHeight) * scale;

        // Premultiplied is what the sweep's surface holds and what Skia produces natively; ffmpeg's
        // rgba is straight alpha, so an unpremultiplied read-back is the one conversion needed. On
        // an opaque render the two are identical, so this costs nothing there.
        var frameBytes = (long)width * height * 4;
        using var readback = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888,
            alpha ? SKAlphaType.Unpremul : SKAlphaType.Premul));

        var sweepFps = spec.SweepFps > 0 ? spec.SweepFps : defaults?.Fps > 0 ? defaults.Fps : cut.Options.DefaultFps;
        var args = BuildArguments(width, height, sweepFps, outputFps, codec, outputPath);
        log.LogInformation("ffmpeg {Args}", args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(cut.Options.FfmpegPath, args)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CutPolicyException(
                $"Could not start ffmpeg at '{cut.Options.FfmpegPath}': {ex.Message}. " +
                "Set Cut:FfmpegPath, or call probe to see what this machine has. The Docker image carries ffmpeg; the release zips do not.");
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var frames = 0;
        SweepReport report;
        try
        {
            var stdin = process.StandardInput.BaseStream;
            report = cut.Sweep(composition, spec, frame =>
            {
                if (!frame.Image.ReadPixels(readback.PeekPixels(), 0, 0))
                    throw new InvalidOperationException($"Could not read frame {frame.Index} back from the render surface.");
                var span = readback.GetPixelSpan();
                if (span.Length != frameBytes)
                    throw new InvalidOperationException($"Frame {frame.Index} is {span.Length} bytes, expected {frameBytes}.");
                stdin.Write(span);
                frames++;
            });
            stdin.Flush();
        }
        catch (IOException ex)
        {
            // A broken pipe means ffmpeg died first, and its stderr says why - which is a far more
            // useful error than "the pipe has been ended".
            KillQuietly(process);
            throw new InvalidOperationException($"ffmpeg stopped reading after {frames} frame(s). {Tail(stderr)}", ex);
        }

        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromSeconds(cut.Options.FfmpegTimeoutSeconds)))
        {
            KillQuietly(process);
            throw new TimeoutException($"ffmpeg did not finish within Cut:FfmpegTimeoutSeconds ({cut.Options.FfmpegTimeoutSeconds}s). {Tail(stderr)}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited {process.ExitCode}. {Tail(stderr)}");

        var info = new FileInfo(outputPath);
        if (!info.Exists) throw new InvalidOperationException($"ffmpeg reported success but wrote no file at '{outputPath}'. {Tail(stderr)}");

        return (new VideoResult(outputPath, info.Length, frames, outputFps, frames / outputFps, codec.Name, alpha), report);
    }

    private static string BuildArguments(int width, int height, double inputFps, double outputFps, VideoCodec codec, string output)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo",
            "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}",
            "-framerate", Rate(inputFps),
            "-i", "pipe:0",
            "-an",
        };

        // Only ask for a rate conversion when the sweep rate and the output rate differ; otherwise
        // ffmpeg is told the same number twice and may drop a frame to honour it.
        if (Math.Abs(inputFps - outputFps) > 1e-9)
        {
            args.Add("-r");
            args.Add(Rate(outputFps));
        }

        args.Add("-c:v");
        args.Add(codec.Encoder);
        args.Add("-pix_fmt");
        args.Add(codec.PixelFormat);
        args.AddRange(codec.ExtraArgs);
        args.Add(output);

        return string.Join(' ', args.Select(Quote));
    }

    private static string Rate(double fps) => fps.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;

    private static string Tail(StringBuilder stderr)
    {
        var text = stderr.ToString().Trim();
        if (text.Length == 0) return "ffmpeg said nothing on stderr.";
        var lines = text.Split('\n');
        return "ffmpeg: " + string.Join(" | ", lines.TakeLast(6).Select(l => l.Trim()));
    }

    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* it is already gone, which is what we wanted */ }
    }

    private static string Run(string exe, string args, TimeSpan timeout)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Could not start '{exe}'.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            KillQuietly(process);
            throw new TimeoutException($"'{exe} {args}' did not answer within {timeout.TotalSeconds:0}s.");
        }
        // ffmpeg writes its banner and its encoder list to different streams depending on version.
        return stdout.Result + stderr.Result;
    }
}

/// <summary>One offered codec: the name a caller uses, the ffmpeg encoder behind it, and whether it
/// carries alpha.</summary>
public sealed record VideoCodec(
    string Name,
    string Encoder,
    string PixelFormat,
    string Extension,
    bool Alpha,
    IReadOnlyList<string> ExtraArgs);
