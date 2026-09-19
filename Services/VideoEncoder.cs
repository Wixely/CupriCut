using System.Diagnostics;
using System.Text;
using CupriCut.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CupriCut.Services;

/// <summary>
/// How transparency reaches the file.
///
/// <para>Only some codecs have an alpha channel at all. H.264 has none — not in Baseline, Main or
/// High, and no amount of pixel-format argument invents one. The industry answer, and the one every
/// web player uses when it has to support Safari, is to carry the alpha as a second image in the
/// same opaque frame and multiply it back at playback.</para>
/// </summary>
public enum AlphaMode
{
    /// <summary>A real alpha channel in the codec. Needs a codec that has one.</summary>
    Embedded,

    /// <summary>Colour on top, alpha as greyscale underneath, in one opaque frame of twice the
    /// height. Works with any codec, H.264 included.</summary>
    MatteBelow,

    /// <summary>Colour on the left, alpha as greyscale on the right, in one opaque frame of twice
    /// the width.</summary>
    MatteRight,

    /// <summary>
    /// The alpha channel ALONE, as a black-and-white video the same size as the colour one.
    ///
    /// <para>Not a way of carrying colour and alpha together - it throws the colour away. It is the
    /// travelling matte an editor asks for when its format will not take a transparent clip at all:
    /// white where the composition is opaque, black where it is not, and the colour rendered
    /// separately. Every editor that exists can key one of these.</para>
    /// </summary>
    MaskOnly,
}

/// <summary>What ffmpeg is on this machine, if anything.</summary>
public sealed record FfmpegInfo(bool Available, string Path, string? Version, IReadOnlyList<string> Encoders, string? Error);

/// <summary>The result of an encode.</summary>
public sealed record VideoResult(string Path, long Bytes, int Frames, double Fps, double Seconds, string Codec, bool Alpha)
{
    /// <summary>How transparency was carried, when it was asked for.</summary>
    public AlphaMode AlphaMode { get; init; }

    /// <summary>The pixel format the finished file actually has - checked rather than assumed.</summary>
    public string? PixelFormat { get; init; }

    /// <summary>What the muxed track actually came out as, read back off the file rather than
    /// assumed from what was asked for. Null when the file carries no audio.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>How to use a matte, for a caller who now has a double-height video and needs to
    /// know why.</summary>
    public string? Note { get; init; }
}

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
    /// <summary>
    /// Encode from a directory of numbered PNGs rather than from a pipe.
    ///
    /// <para>The second half of a parallel render: the frames already exist, in order, so ffmpeg
    /// reads them as an image sequence. Everything about the codec is the same as the piped path -
    /// only the input differs.</para>
    /// </summary>
    public VideoResult EncodeFromFrames(string pattern, int frames, double outputFps, VideoCodec codec,
        string outputPath, bool alpha, AlphaMode alphaMode)
    {
        cut.EnsureVideoAllowed();

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-framerate", Rate(outputFps),
            "-i", pattern,
            "-an",
        };

        var matte = alpha && alphaMode != AlphaMode.Embedded;
        if (matte)
        {
            args.Add("-filter_complex");
            args.Add(MatteFilter(alphaMode));
            args.Add("-map");
            args.Add("[v]");
        }

        args.Add("-c:v");
        args.Add(codec.Encoder);
        var pixelFormat = matte ? "yuv420p" : codec.PixelFormat;
        if (pixelFormat is { Length: > 0 })
        {
            args.Add("-pix_fmt");
            args.Add(pixelFormat);
        }
        if (!matte || codec.Name != "gif") args.AddRange(codec.ExtraArgs);
        args.Add(outputPath);

        var line = string.Join(' ', args.Select(Quote));
        log.LogInformation("ffmpeg {Args}", line);
        RunToCompletion(line);

        var info = new FileInfo(outputPath);
        if (!info.Exists) throw new InvalidOperationException($"ffmpeg reported success but wrote no file at '{outputPath}'.");

        var actualFormat = PixelFormatOf(outputPath);
        if (alpha && alphaMode == AlphaMode.Embedded) VerifyEmbeddedAlpha(codec, actualFormat, outputPath);

        return new VideoResult(outputPath, info.Length, frames, outputFps, frames / outputFps, codec.Name, alpha)
        {
            AlphaMode = alphaMode,
            PixelFormat = actualFormat,
            Note = MatteNote(alpha, alphaMode),
        };
    }

    /// <summary>Run ffmpeg to completion with no stdin, failing loudly on a non-zero exit.</summary>
    private void RunToCompletion(string args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(cut.Options.FfmpegPath, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try { process.Start(); }
        catch (Exception ex)
        {
            throw new CutPolicyException($"Could not start ffmpeg at '{cut.Options.FfmpegPath}': {ex.Message}.");
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        if (!process.WaitForExit(TimeSpan.FromSeconds(cut.Options.FfmpegTimeoutSeconds)))
        {
            KillQuietly(process);
            throw new TimeoutException($"ffmpeg did not finish within Cut:FfmpegTimeoutSeconds. {Tail(stderr)}");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited {process.ExitCode}. {Tail(stderr)}");
    }

    private static string? MatteNote(bool alpha, AlphaMode mode)
    {
        if (!alpha || mode == AlphaMode.Embedded) return null;

        if (mode == AlphaMode.MaskOnly)
            return "This is the MASK, not the artwork: white where the composition is opaque, black where it is not, " +
                   "and no colour at all. Render the same clip again without the mask to get the colour half.";

        return $"Alpha is carried as a matte: the frame is {(mode == AlphaMode.MatteRight ? "twice as wide, colour left and alpha right" : "twice as tall, colour on top and alpha below")}. " +
               "Composite with colour x alpha; the colour half is straight (unpremultiplied) alpha.";
    }

    /// <summary>Codecs offered by name, and what they mean to ffmpeg. Keyed the way an agent would
    /// ask.</summary>
    public static readonly IReadOnlyDictionary<string, VideoCodec> Codecs = new Dictionary<string, VideoCodec>(StringComparer.OrdinalIgnoreCase)
    {
        ["h264"] = new("h264", "libx264", "yuv420p", ".mp4", Alpha: false, ["-preset", "medium", "-crf", "18", "-movflags", "+faststart"]),
        ["h265"] = new("h265", "libx265", "yuv420p", ".mp4", Alpha: false, ["-preset", "medium", "-crf", "22", "-tag:v", "hvc1", "-movflags", "+faststart"]),
        // libopus, not aac: WebM accepts only Vorbis or Opus, and ffmpeg refuses to write the
        // header rather than transcoding for you.
        ["vp9"] = new("vp9", "libvpx-vp9", "yuva420p", ".webm", Alpha: true, ["-b:v", "0", "-crf", "30"], AudioEncoder: "libopus"),
        ["prores"] = new("prores", "prores_ks", "yuva444p10le", ".mov", Alpha: true, ["-profile:v", "4444"]),
        // GIF needs a palette built from the actual frames, not a flat 256-colour conversion. The
        // split/palettegen/paletteuse graph is the difference between a 16 MB posterised mess and
        // a sharp file a fraction of the size, so it is the default rather than an option. No
        // -pix_fmt here: paletteuse decides the format, and passing one as well fights it.
        ["gif"] = new("gif", "gif", PixelFormat: null, ".gif", Alpha: false,
        [
            "-filter_complex", "[0:v]split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3",
            "-loop", "0",
        ], AudioEncoder: null),
    };

    /// <summary>The codec picked when the caller names none: h264 normally, and the first
    /// alpha-capable one when the caller asked for alpha.</summary>
    public static VideoCodec Default(bool alpha) => alpha ? Codecs["vp9"] : Codecs["h264"];

    public static VideoCodec Resolve(string? name, bool alpha) => Resolve(name, alpha, AlphaMode.Embedded);

    public static VideoCodec Resolve(string? name, bool alpha, AlphaMode mode)
    {
        if (string.IsNullOrWhiteSpace(name)) return Default(alpha && mode == AlphaMode.Embedded);
        if (!Codecs.TryGetValue(name, out var codec))
            throw new ArgumentException($"Unknown codec '{name}'. Known codecs: {string.Join(", ", Codecs.Keys)}.", nameof(name));

        // A mask of an opaque render is a white rectangle. Refusing it is kinder than writing one
        // and leaving someone to key against it.
        if (mode == AlphaMode.MaskOnly && !alpha)
            throw new ArgumentException(
                "A mask is the alpha channel, and an opaque render has none - the file would be a plain white rectangle. " +
                "Set alpha:true, which is what gives the mask something to describe.", nameof(name));

        // A matte needs no alpha channel - that is the whole point of it - so only the embedded
        // mode cares what the codec can carry.
        if (alpha && mode == AlphaMode.Embedded && !codec.Alpha)
            throw new ArgumentException(
                $"Codec '{codec.Name}' has no alpha channel, so an embedded-alpha render would be opaque. " +
                $"Either use {string.Join(" or ", Codecs.Values.Where(c => c.Alpha).Select(c => c.Name))}, " +
                $"or keep {codec.Name} and set alphaMode to matteBelow or matteRight, which carries the alpha " +
                "as a second image in the same frame and works with any codec.", nameof(name));

        return codec;
    }

    /// <summary>
    /// Encode two synthetic frames with the <b>real</b> arguments for this codec and mode, and say
    /// what came out.
    ///
    /// <para>The point is to test the command line CupriCut actually issues rather than a
    /// simplified stand-in — a capability that works in isolation and fails in context would be
    /// worse than no check at all. The frames are 64x64 and half transparent, so an alpha channel
    /// that survives has something in it to survive.</para>
    /// </summary>
    /// <param name="pixelFormat">What ffprobe says the finished file is, when it got that far.</param>
    /// <param name="audioCodec">What ffprobe says the muxed track ended up as, or null when the
    /// file has no audio - which is correct for a matte, a mask, a GIF and a silent render.</param>
    public (bool Ok, string Detail) TryTinyEncode(VideoCodec codec, bool alpha, AlphaMode alphaMode, out string? pixelFormat)
    {
        pixelFormat = null;
        const int Size = 64;
        const int Frames = 2;

        string? path = null;
        try
        {
            path = Path.Combine(Path.GetTempPath(), $"cupricut-cal-{Guid.NewGuid():N}"[..30] + codec.Extension);

            var args = BuildArguments(Size, Size, Frames, Frames, codec, path, alpha, alphaMode);
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
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // Left half opaque, right half transparent - so a surviving alpha channel is visibly
            // carrying something rather than merely present.
            var frame = new byte[Size * Size * 4];
            for (var y = 0; y < Size; y++)
                for (var x = 0; x < Size; x++)
                {
                    var i = (y * Size + x) * 4;
                    frame[i] = 0xD9;
                    frame[i + 1] = 0x64;
                    frame[i + 2] = 0x2A;
                    frame[i + 3] = x < Size / 2 ? (byte)0xFF : (byte)0x00;
                }

            try
            {
                for (var f = 0; f < Frames; f++) process.StandardInput.BaseStream.Write(frame);
                process.StandardInput.BaseStream.Flush();
            }
            catch (IOException)
            {
                // ffmpeg rejected the arguments and died before reading; its stderr is the answer.
            }

            process.StandardInput.Close();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                KillQuietly(process);
                return (false, "timed out");
            }

            if (process.ExitCode != 0) return (false, FirstProblem(stderr));
            if (!File.Exists(path)) return (false, "wrote no file");

            pixelFormat = PixelFormatOf(path);
            return (true, pixelFormat ?? "encoded");
        }
        catch (Exception ex)
        {
            return (false, ex.Message.Split('\n')[0]);
        }
        finally
        {
            try { if (path is not null && File.Exists(path)) File.Delete(path); }
            catch { /* a stray temp file is not worth failing a check over */ }
        }
    }

    /// <summary>ffprobe's version line, or null when it is not beside ffmpeg.</summary>
    public string? FfprobeVersion()
    {
        try
        {
            var output = Run(FfprobePath(), "-hide_banner -version", TimeSpan.FromSeconds(10));
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The most useful line of an ffmpeg failure - the last one is usually the cause,
    /// the rest is banner.</summary>
    private static string FirstProblem(StringBuilder stderr)
    {
        var lines = stderr.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        return lines.Length == 0 ? "failed with no message" : lines[^1];
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

    /// <summary>
    /// Render and encode, choosing the fastest honest route.
    ///
    /// <para>A composition pure in <c>t</c> has independent frames, so a long clip is sharded across
    /// cores. An impure one must be swept in order. Short clips go down one thread either way: below
    /// about fifty frames the setup costs more than the sharding saves.</para>
    /// </summary>
    public (VideoResult Video, SweepReport Sweep) EncodeFastest(
        Composition composition, SweepSpec spec, string outputPath, VideoCodec codec,
        double outputFps, AlphaMode alphaMode, int workers, ILogger renderLog,
        string? audioPath = null)
    {
        var target = new ExportTarget(codec.Name, codec, alphaMode, outputPath);

        // The same rule the multi-target path applies, so a single render of a mask does not
        // acquire sound just for having come through a different door.
        if (audioPath is { Length: > 0 } && AudioTrack.Suits(target))
            target = target with { AudioPath = audioPath };

        var (videos, report) = ExportFastest(composition, spec, [target], outputFps, workers, renderLog);
        return (videos[0], report);
    }

    /// <summary>
    /// The same choice of route, producing however many files were asked for from the one render.
    ///
    /// <para>The targets share the sweep, so the second format is very nearly free: what a clip
    /// costs is rendering it, and a frame written to four pipes costs no more to render than a
    /// frame written to one.</para>
    /// </summary>
    public (IReadOnlyList<VideoResult> Videos, SweepReport Sweep) ExportFastest(
        Composition composition, SweepSpec spec, IReadOnlyList<ExportTarget> targets,
        double outputFps, int workers, ILogger renderLog)
    {
        cut.EnsureVideoAllowed();

        // Resolved HERE as well as in the sweep, because the parallel path opens its own documents
        // straight from this composition and never passes through Sweep. Applying it twice is a
        // no-op - the composition remembers.
        composition = Timeline.Apply(Backdrop.Resolve(composition, spec.ShowBackground));

        var purity = Purity.Analyse(composition);
        var wanted = ParallelRenderer.Resolve(workers, cut.Options.RenderWorkers);

        if (!purity.PureInTime || spec.Times.Count < ParallelRenderer.WorthSharding || wanted == 1)
            return EncodeAll(composition, spec, targets, outputFps);

        // Same pipes as the sequential path - only the number of threads feeding them differs.
        var renderer = new ParallelRenderer(cut, renderLog);
        ShardReport? shard = null;
        var videos = Fan(composition, spec, targets, outputFps, (write, _) =>
        {
            shard = renderer.RenderInOrder(composition, spec, spec.Times, wanted, (_, span) => write(span));
            return shard.Frames;
        });

        var report = new SweepReport(
            composition.Path,
            shard!.Frames, shard.Frames, outputFps, spec.Times[^1], shard.ElapsedMs, true, [])
        {
            Purity = purity,
            Workers = shard.Workers,
            Present = Presentation.Resolve(spec, composition.Defaults, cut.Options),
        };

        return (videos, report);
    }
    /// <summary>
    /// Start one ffmpeg per target, let <paramref name="fill"/> produce raw frames, and write every
    /// frame into all of them.
    ///
    /// <para>This is the only place a rendered clip becomes a file, and everything that used to be
    /// written out three times - starting the process, holding its stderr, telling a broken pipe
    /// from a bad exit, probing what actually came out - happens once here.</para>
    ///
    /// <para><b>One render, several files.</b> A frame costs the same to write to four pipes as to
    /// one, and the render is what the time goes on, so an mp4 alongside its mask alongside a GIF
    /// costs one sweep rather than three. The targets share the render, which means they share its
    /// alpha: with alpha on, an opaque target is flattened onto black by its own pixel format, and
    /// black-plus-matte is exactly the pair an editor keys.</para>
    /// </summary>
    private IReadOnlyList<VideoResult> Fan(Composition composition, SweepSpec spec,
        IReadOnlyList<ExportTarget> targets, double outputFps, FrameSource fill)
    {
        if (targets.Count == 0) throw new ArgumentException("An export needs at least one target.", nameof(targets));

        var (present, alpha, sweepFps) = Geometry(composition, spec);
        var width = present.OutputWidth;
        var height = present.OutputHeight;
        var frameBytes = (int)present.FrameBytes;

        var sinks = new List<FfmpegSink>(targets.Count);
        try
        {
            foreach (var target in targets)
            {
                var args = BuildArguments(width, height, sweepFps, outputFps, target.Codec, target.Path,
                    alpha, target.AlphaMode, target.AudioPath);
                sinks.Add(FfmpegSink.Start(cut.Options.FfmpegPath, args, target.Name, target.Path, log));
            }

            var written = 0;
            void Write(ReadOnlySpan<byte> frame)
            {
                if (frame.Length != frameBytes)
                    throw new InvalidOperationException($"A frame is {frame.Length} bytes, expected {frameBytes}.");

                for (var i = 0; i < sinks.Count; i++)
                {
                    try { sinks[i].Input.Write(frame); }
                    catch (IOException ex) { throw sinks[i].Broke(ex, written); }
                }
                written++;
            }

            var frames = fill(Write, frameBytes);
            foreach (var sink in sinks) sink.Finish(cut.Options.FfmpegTimeoutSeconds, frames);

            var results = new List<VideoResult>(targets.Count);
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];

                // Asked of the TARGET, not of the path: a PNG sequence's path is a pattern, and
                // File.Exists on a pattern is false however many frames landed beside it.
                var bytes = target.SizeOnDisk();
                if (bytes == 0)
                    throw new InvalidOperationException(
                        $"ffmpeg ({target.Name}) reported success but wrote nothing at '{target.Path}'. {sinks[i].Tail()}");

                var actualFormat = PixelFormatOf(target.Path);

                // Only a target that was SUPPOSED to come out transparent. An export of mp4
                // alongside mask renders once with alpha and writes both, and the mp4 losing its
                // alpha channel is the point of it - that pair is the colour and its matte. The
                // check still fires for a format that claims to carry alpha and then does not,
                // which is the failure it was written for.
                if (alpha && target.AlphaMode == AlphaMode.Embedded && target.Codec.Alpha)
                    VerifyEmbeddedAlpha(target.Codec, actualFormat, target.Path);

                // The same rule as alpha, for the same reason: validating the request was never
                // enough. A container that will not take the audio codec it was handed can refuse
                // loudly - which is how WebM-with-AAC was found - or drop the stream and leave a
                // silent file that looks exactly like a correct one.
                var actualAudio = target.AudioPath is { Length: > 0 } ? AudioCodecOf(target.Path) : null;

                if (target.AudioPath is { Length: > 0 } && actualAudio is null)
                {
                    throw new InvalidOperationException(
                        $"A track was muxed into {target.Name} ('{target.Path}') and the finished file has no "
                        + $"audio stream - this ffmpeg dropped it. The {target.Codec.Extension} container was "
                        + $"asked for {target.Codec.AudioEncoder}. {sinks[i].Tail()}");
                }

                results.Add(new VideoResult(target.Path, bytes, frames, outputFps,
                    frames / outputFps, target.Codec.Name, alpha)
                {
                    AlphaMode = target.AlphaMode,
                    PixelFormat = actualFormat,
                    AudioCodec = actualAudio,
                    Note = target.Note ?? MatteNote(alpha, target.AlphaMode),
                });
            }
            return results;
        }
        finally
        {
            foreach (var sink in sinks) sink.Dispose();
        }
    }

    /// <summary>The geometry and alpha a spec resolves to. Asked of <see cref="Presentation"/>
    /// rather than worked out here: the pipe has to be told the EXACT frame size the sweep will
    /// produce, and a disagreement is not an error - it is a stream of misaligned bytes that
    /// encodes to a sheared video.</summary>
    private (Presentation Present, bool Alpha, double SweepFps) Geometry(Composition composition, SweepSpec spec)
    {
        var defaults = composition.Defaults;
        return (
            Presentation.Resolve(spec, defaults, cut.Options),
            spec.Alpha ?? defaults?.Alpha ?? false,
            spec.SweepFps > 0 ? spec.SweepFps : defaults?.Fps > 0 ? defaults.Fps : cut.Options.DefaultFps);
    }

    /// <summary>Sweep the composition and pipe every kept frame into ffmpeg.</summary>
    public (VideoResult Video, SweepReport Sweep) Encode(SweepSpec spec, string outputPath, VideoCodec codec, double outputFps) =>
        Encode(cut.LoadComposition(spec.Composition), spec, outputPath, codec, outputFps, AlphaMode.Embedded);

    public (VideoResult Video, SweepReport Sweep) Encode(Composition composition, SweepSpec spec, string outputPath, VideoCodec codec, double outputFps) =>
        Encode(composition, spec, outputPath, codec, outputFps, AlphaMode.Embedded);

    /// <summary>The same encode over a composition already in hand, so a project's render defaults
    /// are read once rather than by loading it twice.</summary>
    public (VideoResult Video, SweepReport Sweep) Encode(Composition composition, SweepSpec spec,
        string outputPath, VideoCodec codec, double outputFps, AlphaMode alphaMode)
    {
        var (video, report) = EncodeAll(composition, spec,
            [new ExportTarget(codec.Name, codec, alphaMode, outputPath)], outputFps);
        return (video[0], report);
    }

    /// <summary>Sweep once on this thread and feed every target from it.</summary>
    public (IReadOnlyList<VideoResult> Videos, SweepReport Sweep) EncodeAll(Composition composition, SweepSpec spec,
        IReadOnlyList<ExportTarget> targets, double outputFps)
    {
        cut.EnsureVideoAllowed();
        composition = Timeline.Apply(Backdrop.Resolve(composition, spec.ShowBackground));

        var (present, alpha, _) = Geometry(composition, spec);

        // Premultiplied is what the sweep's surface holds and what Skia produces natively; ffmpeg's
        // rgba is straight alpha, so an unpremultiplied read-back is the one conversion needed. On
        // an opaque render the two are identical, so this costs nothing there.
        using var readback = new SKBitmap(new SKImageInfo(present.OutputWidth, present.OutputHeight,
            SKColorType.Rgba8888, alpha ? SKAlphaType.Unpremul : SKAlphaType.Premul));

        SweepReport report = null!;
        var frames = 0;
        var videos = Fan(composition, spec, targets, outputFps, (write, _) =>
        {
            report = cut.Sweep(composition, spec, frame =>
            {
                if (!frame.Image.ReadPixels(readback.PeekPixels(), 0, 0))
                    throw new InvalidOperationException($"Could not read frame {frame.Index} back from the render surface.");
                write(readback.GetPixelSpan());
                frames++;
            });
            return frames;
        });

        return (videos, report);
    }

    /// <summary>
    /// The filter that packs colour and alpha into one opaque frame.
    ///
    /// <para>Colour keeps its own pixels; the alpha becomes a greyscale image beside or below it.
    /// A player composites with <c>colour * alpha</c>. This is what lets H.264 - which has no alpha
    /// channel at all - deliver transparency, and it is what web players do for Safari.</para>
    ///
    /// <para>The colour half is straight (unpremultiplied) alpha, which is what the sweep produces
    /// when alpha is asked for, so the multiply at playback is correct rather than doubled.</para>
    /// </summary>
    private static string MatteFilter(AlphaMode mode)
    {
        // The mask keeps only the alpha. gray rather than rgba because there is nothing to stack it
        // against, and greyscale is what the yuv420p conversion downstream wants anyway.
        if (mode == AlphaMode.MaskOnly) return "[0:v]format=rgba,alphaextract,format=gray[v]";

        var stack = mode == AlphaMode.MatteRight ? "hstack" : "vstack";
        return "[0:v]format=rgba,split=2[c][a];" +
               "[a]alphaextract,format=rgba[am];" +
               $"[c][am]{stack}=inputs=2[v]";
    }

    /// <summary>
    /// Confirm the finished file really carries alpha.
    ///
    /// <para>Choosing an alpha-capable codec is not the same as getting alpha out the other end.
    /// Measured here: libvpx-vp9 on ffmpeg N-91454 (2018) accepts <c>-pix_fmt yuva420p</c>, reports
    /// success, and writes plain <c>yuv420p</c> - a silently opaque "transparent" video, which is
    /// the exact outcome the codec check was written to prevent. Validating the REQUEST was never
    /// enough; this validates the RESULT.</para>
    /// </summary>
    private void VerifyEmbeddedAlpha(VideoCodec codec, string? actualFormat, string outputPath)
    {
        if (actualFormat is null)
        {
            log.LogWarning("Could not read the pixel format of {Path}; alpha is unverified", outputPath);
            return;
        }

        if (HasAlpha(actualFormat)) return;

        var alternatives = string.Join(" or ", Codecs.Values.Where(c => c.Alpha).Select(c => c.Name));
        throw new InvalidOperationException(
            $"Alpha was requested and {codec.Name} ({codec.Encoder}) accepted it, but the finished file is '{actualFormat}', " +
            $"which has no alpha channel - this ffmpeg dropped it silently. The file has been left at '{outputPath}' so you can see for yourself. " +
            $"Either use {alternatives} if one of them works on this ffmpeg, or set alphaMode to matteBelow / matteRight, " +
            "which carries the alpha as a second image in the same frame and does not depend on codec alpha support at all.");
    }

    /// <summary>A pixel format with an alpha component. ffmpeg names them consistently enough that
    /// the name is the test: yuva*, *bgra, rgba, argb, abgr, ya*, pal8.</summary>
    public static bool HasAlpha(string pixelFormat) =>
        pixelFormat.StartsWith("yuva", StringComparison.OrdinalIgnoreCase)
        || pixelFormat.StartsWith("gbra", StringComparison.OrdinalIgnoreCase)
        || pixelFormat.StartsWith("ya", StringComparison.OrdinalIgnoreCase)
        || pixelFormat.Contains("rgba", StringComparison.OrdinalIgnoreCase)
        || pixelFormat.Contains("argb", StringComparison.OrdinalIgnoreCase)
        || pixelFormat.Contains("abgr", StringComparison.OrdinalIgnoreCase)
        || pixelFormat.Contains("bgra", StringComparison.OrdinalIgnoreCase);

    /// <summary>The pixel format ffprobe reports for a finished file, or null when it cannot be
    /// asked. ffprobe sits beside ffmpeg in every distribution that ships both.</summary>
    private string? PixelFormatOf(string path)
    {
        try
        {
            var probe = FfprobePath();
            var output = Run(probe, $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{path}\"", TimeSpan.FromSeconds(15));
            var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "ffprobe could not read the pixel format of {Path}", path);
            return null;
        }
    }

    /// <summary>The audio codec ffprobe reports for a finished file, or null when it has no audio
    /// stream at all - which for a muxed target is a failure and for every other one is correct.</summary>
    public string? AudioCodecOf(string path)
    {
        try
        {
            var probe = FfprobePath();
            var output = Run(probe, $"-v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 \"{path}\"", TimeSpan.FromSeconds(15));
            var value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "ffprobe could not read the audio stream of {Path}", path);
            return null;
        }
    }

    private string FfprobePath()
    {
        var ffmpeg = cut.Options.FfmpegPath;
        var directory = Path.GetDirectoryName(ffmpeg);
        var name = Path.GetFileName(ffmpeg).Replace("ffmpeg", "ffprobe", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    private static string BuildArguments(int width, int height, double inputFps, double outputFps,
        VideoCodec codec, string output, bool alpha, AlphaMode mode, string? audioPath = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo",
            "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}",
            "-framerate", Rate(inputFps),
            "-i", "pipe:0",
        };

        // The track is a SECOND input, so it must be declared before any output option. With no
        // track this stays -an, which is what it always was: an encoder fed raw frames has no
        // audio to drop, and saying so keeps ffmpeg from looking for one.
        var muxing = audioPath is { Length: > 0 } && codec.AudioEncoder is { Length: > 0 };
        if (muxing)
        {
            args.Add("-i");
            args.Add(Quote(audioPath!));
        }
        else
        {
            args.Add("-an");
        }

        // Only ask for a rate conversion when the sweep rate and the output rate differ; otherwise
        // ffmpeg is told the same number twice and may drop a frame to honour it.
        if (Math.Abs(inputFps - outputFps) > 1e-9)
        {
            args.Add("-r");
            args.Add(Rate(outputFps));
        }

        var matte = alpha && mode != AlphaMode.Embedded;
        if (matte)
        {
            args.Add("-filter_complex");
            args.Add(MatteFilter(mode));
            args.Add("-map");
            args.Add("[v]");
        }

        args.Add("-c:v");
        args.Add(codec.Encoder);

        // A matte is an OPAQUE frame, so the codec's alpha pixel format would be wrong for it -
        // and h264/h265 have none anyway. yuv420p is what plays everywhere.
        var pixelFormat = matte ? "yuv420p" : codec.PixelFormat;
        if (pixelFormat is { Length: > 0 })
        {
            args.Add("-pix_fmt");
            args.Add(pixelFormat);
        }

        // The gif codec brings its own filter_complex; two of them cannot coexist.
        if (!matte || codec.Name != "gif") args.AddRange(codec.ExtraArgs);

        if (muxing)
        {
            // The frames are mapped explicitly too. Without it ffmpeg picks one stream per type
            // from the inputs by its own rules, and a filter_complex output means the rules do not
            // pick the one that was just built.
            if (!matte)
            {
                args.Add("-map");
                args.Add("0:v:0");
            }

            args.Add("-map");
            args.Add("1:a:0");
            args.Add("-c:a");
            args.Add(codec.AudioEncoder!);
            args.Add("-b:a");
            args.Add("192k");

            // -shortest, so a track longer than the animation does not extend the clip with a
            // still frame, and one shorter does not leave the end silent-but-running. The picture
            // is the thing being made; the audio accompanies it.
            args.Add("-shortest");
        }

        args.Add(output);

        return string.Join(' ', args.Select(Quote));
    }

    private static string Rate(double fps) => fps.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    private static string Quote(string arg) =>
        arg.AsSpan().ContainsAny(" ;[]()'") ? $"\"{arg}\"" : arg;

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
/// <param name="AudioEncoder">What to encode a muxed track with, or null when the container
/// takes no audio at all. A CONTAINER property, not a preference: WebM accepts only Vorbis or
/// Opus and rejects AAC outright, and GIF has no audio stream to put anything in. Discovered by
/// exporting a keying pair and watching ffmpeg refuse to write the header.</param>
public sealed record VideoCodec(
    string Name,
    string Encoder,
    string? PixelFormat,
    string Extension,
    bool Alpha,
    IReadOnlyList<string> ExtraArgs,
    string? AudioEncoder = "aac");
