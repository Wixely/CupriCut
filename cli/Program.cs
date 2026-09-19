using System.Globalization;
using CupriCut.Configuration;
using CupriCut.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace CupriCut.Cli;

/// <summary>
/// The <c>cupricut</c> command line, over exactly the services the MCP server uses.
///
/// <para>Same sweep, same roots, same limits, same configuration files - the only difference is who
/// is asking. Anything true of a frame the server rendered is true of a frame this rendered.</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 1 : 0;
        }

        var verb = args[0];
        var opts = CommandLine.Parse(args.AsSpan(1));

        try
        {
            var (cut, encoder) = Build(opts);
            return verb switch
            {
                "frame" => Frame(cut, opts),
                "sheet" => Sheet(cut, opts),
                "frames" => Frames(cut, opts),
                "video" => Video(cut, encoder, opts),
                "export" => Export(cut, encoder, opts),
                "formats" => Formats(),
                "probe" => Probe(cut, encoder),
                "inspect" => Inspect(cut, opts),
                "lint" => Lint(cut, opts),
                "audio" => Audio(cut, opts),
                "calibrate" => Calibrate(cut, encoder, opts),
                "fonts" => Fonts(cut, opts),
                "projects" => Projects(cut),
                "project" => Project(cut, opts),
                _ => Unknown(verb),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cupricut: {ex.Message}");
            if (opts.Has("verbose")) Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ---- verbs -----------------------------------------------------------------------------

    private static int Frame(CupriCutService cut, CommandLine opts)
    {
        var loaded = cut.LoadComposition(opts.Require("composition"));
        var t = opts.Number("t", 0);
        var output = opts.Text("out") ?? $"frame-t{t.ToString("0.000", CultureInfo.InvariantCulture).Replace('.', 'p')}.png";
        byte[]? png = null;

        var report = cut.Sweep(loaded, Spec(opts, [t]), frame => png = FrameEncoder.Encode(frame.Image));
        var path = cut.ResolveWrite(output);
        File.WriteAllBytes(path, png!);

        Console.WriteLine($"{path}  ({png!.Length:N0} bytes)");
        Report(report);
        return 0;
    }

    private static int Sheet(CupriCutService cut, CommandLine opts)
    {
        var loaded = cut.LoadComposition(opts.Require("composition"));
        var times = opts.Numbers("times");
        var duration = opts.Number("duration", loaded.Defaults?.Duration ?? 3);
        var count = (int)opts.Number("count", 9);
        var every = opts.Has("every") ? opts.Number("every", 0) : (double?)null;
        var thumb = (int)opts.Number("thumb", 320);
        var alpha = opts.Has("alpha") || (loaded.Defaults?.Alpha ?? false);

        var sample = times.Length > 0 ? times : Sample(duration, every, count);
        var cells = new List<(SKBitmap Frame, double Time)>();
        SweepReport report;
        try
        {
            report = cut.Sweep(loaded, Spec(opts, sample), frame => cells.Add((Thumbnail(FrameEncoder.Copy(frame.Image), thumb), frame.Time)));
        }
        catch
        {
            foreach (var (frame, _) in cells) frame.Dispose();
            throw;
        }

        var columns = opts.Has("columns") ? (int)opts.Number("columns", 0) : (int)Math.Ceiling(Math.Sqrt(cells.Count));
        var png = ContactSheet.Compose(cells, columns, alpha);
        var path = cut.ResolveWrite(opts.Text("out") ?? "contact-sheet.png");
        File.WriteAllBytes(path, png);

        Console.WriteLine($"{path}  ({png.Length:N0} bytes, {sample.Length} cells, {columns} columns)");
        Report(report);
        return 0;
    }

    private static int Frames(CupriCutService cut, CommandLine opts)
    {
        var loaded = cut.LoadComposition(opts.Require("composition"));
        var fps = opts.Number("fps", loaded.Defaults?.Fps ?? cut.Options.DefaultFps);
        var from = opts.Number("from", 0);
        double[] times;
        ClipPlan? plan = null;
        if (opts.Has("to")) times = Range(from, opts.Number("to", 1), fps);
        else (times, plan) = ClipPlanner.Plan(from, opts.Number("duration", loaded.Defaults?.Duration ?? 1), fps);
        var stem = ProjectStem(opts.Text("out") ?? opts.Require("composition"));
        var folder = opts.Text("out") is { Length: > 0 } ? stem : OutputNaming.Run(stem);

        var directory = Path.GetDirectoryName(cut.ResolveWrite(Path.Combine(folder, ".keep")))!;
        // Streamed in bounded memory - see FrameSequenceWriter. Collecting the run would need
        // 3.7 MB a frame, which a long sequence does not have.
        using var writer = new FrameSequenceWriter();
        var report = cut.Sweep(loaded, Spec(opts, times, fps),
            frame => writer.Add(frame.Image, Path.Combine(directory, $"{stem}_{frame.Index:D5}.png")));

        var written = writer.Complete();
        Console.WriteLine($"{directory}  ({written.Length} PNGs)");
        ReportEvents(loaded, directory, stem, fps, times.Length / Math.Max(fps, 1));
        ReportTiming(plan);
        Report(report);
        return 0;
    }

    /// <summary>One render, every format asked for. The second format is very nearly free - a
    /// frame written to four pipes costs no more to produce than a frame written to one - so this
    /// is the verb to reach for whenever more than one file is wanted.</summary>
    private static int Export(CupriCutService cut, VideoEncoder encoder, CommandLine opts)
    {
        cut.EnsureVideoAllowed();
        var loaded = cut.LoadComposition(opts.Require("composition"));
        var defaults = loaded.Defaults;
        var fps = opts.Number("fps", defaults?.Fps ?? cut.Options.DefaultFps);
        var from = opts.Number("from", 0);
        var alpha = opts.Has("alpha") || (defaults?.Alpha ?? false);

        if (opts.Has("duration") && opts.Has("to"))
            throw new ArgumentException("Give either --duration or --to, not both - they differ by one frame.");

        double[] times;
        ClipPlan? plan = null;
        if (opts.Has("to")) times = Range(from, opts.Number("to", 3), fps);
        else (times, plan) = ClipPlanner.Plan(from, opts.Number("duration", defaults?.Duration ?? 3), fps);

        var wanted = (opts.Text("formats") ?? "mp4")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stem = ProjectStem(opts.Text("out") ?? opts.Require("composition"));
        var folder = opts.Text("out") is { Length: > 0 } ? stem : OutputNaming.Run(stem);
        var directory = Path.GetDirectoryName(cut.ResolveWrite(Path.Combine(folder, ".keep")))!;
        Directory.CreateDirectory(directory);

        using var track = AudioTrack.For(cut, loaded, opts.Text("audio"), !opts.Has("no-audio"));

        var export = ExportFormats.Plan(wanted, opts.Has("alpha") ? true : null, defaults?.Alpha ?? false,
            name => Path.Combine(directory, stem + "_" + name), track.Path);

        // export.Alpha rather than whatever Spec worked out from --alpha: the targets were built
        // against this value, and a render that disagreed would write a "mask" that was not one.
        var (videos, report) = encoder.ExportFastest(
            loaded, Spec(opts, times, fps) with { Alpha = export.Alpha },
            export.Targets, fps, (int)opts.Number("workers", 0), cut.RenderLog);

        Console.WriteLine($"{directory}  (alpha {(export.Alpha ? "on" : "off")})"
                          + (track.Any ? $", audio {track.Source}" : ""));
        for (var i = 0; i < export.Targets.Count; i++)
        {
            var video = videos[i];
            Console.WriteLine($"  {export.Targets[i].Name,-7} {Path.GetFileName(video.Path),-24} {video.Bytes,12:N0} bytes  {video.PixelFormat}");
            if (video.Note is { Length: > 0 }) Console.WriteLine($"          {video.Note}");
        }
        ReportEvents(loaded, directory, stem, fps, times.Length / Math.Max(fps, 1));
        ReportTiming(plan);
        Report(report);
        return 0;
    }

    /// <summary>What a composition is, as a table rather than as JSON.</summary>
    private static int Inspect(CupriCutService cut, CommandLine opts)
    {
        var x = Inspector.Examine(cut, opts.Require("composition"));

        Console.WriteLine($"{Path.GetFileName(x.Composition)}  {x.Width}x{x.Height} at {x.Fps:0.##} fps" +
                          (x.Duration > 0 ? $", {x.Duration:0.###}s" : ""));
        Console.WriteLine(x.Purity.PureInTime
            ? "  pure in t - any frame renders directly"
            : $"  NOT pure in t - {x.Purity.Summary}");

        if (x.Timeline.Any)
        {
            Console.WriteLine();
            Console.WriteLine($"TIMELINE  {x.Timeline.Duration:0.###}s" +
                              (x.Timeline.Tracks.Count > 0 ? $", tracks: {string.Join(", ", x.Timeline.Tracks)}" : ""));
            foreach (var w in x.Timeline.Windows)
            {
                var end = double.IsPositiveInfinity(w.End) ? "end" : $"{w.End:0.###}s";
                Console.WriteLine($"  {w.Start,8:0.###}s -> {end,-9}  {w.Describe()}");
            }
        }

        if (x.Events.Any)
        {
            Console.WriteLine();
            Console.WriteLine($"EVENTS  {x.Events.Events.Count} at {x.Fps:0.##} fps");
            foreach (var e in x.Events.Events)
            {
                var past = x.Duration > 0 && e.At > x.Duration ? "  (past the end)" : "";
                Console.WriteLine($"  {e.At,8:0.###}s  frame {e.Frame(x.Fps),-6}  {e.Describe()}{past}");
            }
        }

        if (x.Backdrops.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("BACKDROP");
            foreach (var b in x.Backdrops) Console.WriteLine($"  {b.Describe()}");
        }

        if (x.References.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("REFERENCES");
            foreach (var r in x.References) Console.WriteLine($"  {r}");
        }

        Console.WriteLine();
        Console.WriteLine("FONTS");
        foreach (var f in x.Fonts)
            Console.WriteLine($"  {(f.MachineDependent ? "!" : " ")} {f.Asked,-20} {f.Weight,4}  -> {f.Answered ?? "(nothing)"} ({f.Source})");
        if (x.Fonts.Count == 0) Console.WriteLine("  (none asked for)");

        return 0;
    }

    /// <summary>Where the hits are in a track, as a table to read before writing keyframes.</summary>
    private static int Audio(CupriCutService cut, CommandLine opts)
    {
        cut.EnsureVideoAllowed();

        var fps = opts.Number("fps", cut.Options.DefaultFps);
        var path = cut.ResolveRead(opts.Require("audio"));
        var analysis = AudioDecoder.Analyse(
            cut.Options.FfmpegPath, path, fps, opts.Number("max-seconds", 1800));

        Console.WriteLine($"{analysis.Source}  {analysis.Seconds:0.###}s at {analysis.Fps:0.##} fps "
                          + $"({(int)Math.Round(analysis.Seconds * analysis.Fps)} frames)");

        // The confidence first and plainly, because every other number here is only as good as it.
        Console.WriteLine(analysis.Tempo.Usable
            ? $"  {analysis.Tempo.Bpm:0.##} BPM, confidence {analysis.Tempo.Confidence:0.00} - beats emitted"
            : $"  no usable beat (search said {analysis.Tempo.Bpm:0.##} BPM at confidence {analysis.Tempo.Confidence:0.00}) "
              + "- use onsets and sound boundaries");

        Console.WriteLine();
        Console.WriteLine(string.Join("   ", Enum.GetValues<CueKind>()
            .Select(k => $"{k.ToString().ToLowerInvariant()} {analysis.Count(k)}")));

        var kind = opts.Text("kind");
        var cues = kind is { Length: > 0 }
            ? analysis.Cues.Where(c => c.Kind.ToString().Equals(kind.Replace("-", ""), StringComparison.OrdinalIgnoreCase))
            : analysis.Cues;

        // --project stores the cues rather than only printing them, which is the difference
        // between looking at a track and timing something to it.
        if (opts.Text("project") is { Length: > 0 } project)
        {
            var loaded = cut.LoadProject(project);
            var embed = !opts.Has("no-embed");

            // --as moves the project as it saves it, which is how a .cut.json that predates the
            // container becomes a .cutpkg at the moment it first has a reason to be one.
            var target = opts.Text("as") is { Length: > 0 } renamed ? renamed : project;

            if (embed)
            {
                var key = Path.GetFileName(path);
                loaded.Assets[key] = $"data:{CutPackage.MediaTypeOf(path)};base64,"
                                     + Convert.ToBase64String(File.ReadAllBytes(path));
                loaded.Audio = ProjectAudio.From(analysis, AudioDecoder.Hash(path), key);
            }
            else
            {
                loaded.Audio = ProjectAudio.From(analysis, AudioDecoder.Hash(path), null);
            }

            var saved = cut.SaveProject(target, loaded);
            Console.WriteLine($"  stored in {saved} ({new FileInfo(saved).Length:N0} bytes"
                              + $"{(embed ? ", track included" : ", cues only")})");
        }

        Console.WriteLine();
        Console.WriteLine("      time  frame   kind        strength  snap");
        foreach (var c in cues)
        {
            Console.WriteLine($"  {c.At,8:0.###}s {c.Frame,6}  {c.Kind,-10}  {c.Strength,8:0.00}"
                              + $"  {c.SnapMs,+6:0.#}ms");
        }

        return 0;
    }

    /// <summary>The determinism verdict. Non-zero on errors, so a CI step can gate on it.</summary>
    private static int Lint(CupriCutService cut, CommandLine opts)
    {
        var x = Inspector.Examine(cut, opts.Require("composition"));
        var all = opts.Has("all");
        var shown = all ? x.Findings : [.. x.Findings.Where(f => f.Level != FindingLevel.Info)];

        foreach (var f in shown)
        {
            var where = f.Line > 0 ? $" (line {f.Line})" : "";
            Console.WriteLine($"{f.Level.ToString().ToLowerInvariant(),-7} {f.Code}{where}: {f.What}");
            if (f.Fix is { Length: > 0 }) Console.WriteLine($"        -> {f.Fix}");
        }

        var hidden = x.Findings.Count - shown.Count;
        Console.WriteLine();
        Console.WriteLine(x.Verdict switch
        {
            "clean" => $"{Path.GetFileName(x.Composition)}: clean.",
            "warnings" => $"{Path.GetFileName(x.Composition)}: {x.Warnings} warning(s).",
            _ => $"{Path.GetFileName(x.Composition)}: {x.Errors} error(s), {x.Warnings} warning(s).",
        });
        if (hidden > 0 && !all) Console.WriteLine($"  {hidden} informational finding(s) hidden. Pass --all for them.");

        // Non-zero only on errors: a warning is a thing to know, not a thing to stop a build.
        return x.Errors > 0 ? 5 : 0;
    }

    private static int Formats()
    {
        foreach (var format in ExportFormats.Describe())
        {
            var name = format.GetType().GetProperty("format")!.GetValue(format);
            var what = format.GetType().GetProperty("what")!.GetValue(format);
            Console.WriteLine($"  {name,-7} {what}");
        }
        return 0;
    }

    private static int Video(CupriCutService cut, VideoEncoder encoder, CommandLine opts)
    {
        cut.EnsureVideoAllowed();
        var loaded = cut.LoadComposition(opts.Require("composition"));
        var defaults = loaded.Defaults;
        var fps = opts.Number("fps", defaults?.Fps ?? cut.Options.DefaultFps);
        var from = opts.Number("from", 0);
        var alpha = opts.Has("alpha") || (defaults?.Alpha ?? false);

        // --duration is a LENGTH: duration x fps frames exactly. --to is an inclusive endpoint,
        // one frame longer. A project's duration is a length.
        if (opts.Has("duration") && opts.Has("to"))
            throw new ArgumentException("Give either --duration or --to, not both - they differ by one frame.");

        double[] times;
        ClipPlan? plan = null;
        if (opts.Has("to")) times = Range(from, opts.Number("to", 3), fps);
        else (times, plan) = ClipPlanner.Plan(from, opts.Number("duration", defaults?.Duration ?? 3), fps);
        var alphaMode = ParseAlphaMode(opts.Text("alpha-mode"));
        var codec = VideoEncoder.Resolve(opts.Text("codec") ?? defaults?.Codec, alpha, alphaMode);
        var stem = ProjectStem(opts.Text("out") ?? opts.Require("composition"));
        var path = cut.ResolveWrite(opts.Text("out") is { Length: > 0 }
            ? stem + codec.Extension
            : OutputNaming.File(stem, codec.Extension));

        using var track = AudioTrack.For(cut, loaded, opts.Text("audio"), !opts.Has("no-audio"));

        var (video, report) = encoder.EncodeFastest(loaded, Spec(opts, times, fps), path, codec, fps,
            alphaMode, (int)opts.Number("workers", 0), cut.RenderLog, track.Path);

        Console.WriteLine($"{video.Path}  ({video.Bytes:N0} bytes, {video.Frames} frames, {video.Seconds:0.######}s, {codec.Name}, {video.PixelFormat})");
        if (video.Note is { Length: > 0 }) Console.WriteLine($"  {video.Note}");
        if (track.Any) Console.WriteLine($"  audio: {track.Source}");
        ReportEvents(loaded, Path.GetDirectoryName(video.Path)!,
            Path.GetFileNameWithoutExtension(video.Path), fps, video.Seconds);
        ReportTiming(plan);
        Report(report);
        return 0;
    }

    /// <summary>The events file a render leaves behind, when the composition declared any.
    ///
    /// <para>Written at render time rather than on request because the frame numbers only exist
    /// once the rate is known, and the rate is a property of the render and not of the
    /// composition. The same marks at 25 fps are different frames.</para>
    ///
    /// <para>Nothing is written when there are no events, so a directory of ordinary renders does
    /// not fill up with empty files.</para></summary>
    private static void ReportEvents(
        Composition loaded, string directory, string stem, double fps, double seconds)
    {
        if (Events.WriteSidecar(loaded.Html, Path.GetFileName(loaded.Path), directory, stem, fps, seconds)
            is not { } path)
        {
            return;
        }

        var plan = Events.Plan(loaded.Html);
        Console.WriteLine($"  {Path.GetFileName(path)}  ({plan.Events.Count} event(s): "
                          + $"{string.Join(", ", plan.Names.Take(4))}{(plan.Names.Count > 4 ? ", ..." : "")})");
    }

    private static int Probe(CupriCutService cut, VideoEncoder encoder)
    {
        Console.WriteLine($"engine        CupriFace {typeof(CupriFace.CupriDocument).Assembly.GetName().Version}");
        Console.WriteLine($"content root  {cut.ContentRoot}");
        Console.WriteLine($"compositions  {(cut.CompositionRoots.Count == 0 ? "(none found)" : string.Join(", ", cut.CompositionRoots))}");
        Console.WriteLine($"output        {cut.OutputRoot}");
        Console.WriteLine($"limits        {cut.Options.MaxFrames} frames, {cut.Options.MaxPixels:N0} pixels/frame");

        if (!cut.Options.EnableVideo)
        {
            Console.WriteLine("video         disabled (Cut:EnableVideo)");
            return 0;
        }

        var ffmpeg = encoder.Probe();
        Console.WriteLine(ffmpeg.Available
            ? $"ffmpeg        {ffmpeg.Version}\ncodecs        {string.Join(", ", ffmpeg.Encoders)}"
            : $"ffmpeg        NOT FOUND at '{ffmpeg.Path}' - {ffmpeg.Error}");
        return ffmpeg.Available ? 0 : 2;
    }

    private static int Calibrate(CupriCutService cut, VideoEncoder encoder, CommandLine opts)
    {
        var calibrator = new Calibrator(cut, encoder, cut.RenderLog);
        var report = calibrator.Run(tuneWorkers: !opts.Has("no-benchmark"));

        Console.WriteLine(report.ToTable(errorsOnly: !opts.Has("all")));

        if (calibrator.BestWorkers is { } best)
        {
            Console.WriteLine();
            Console.WriteLine($"Best render parallelism: {best} workers.");

            if (opts.Has("apply"))
            {
                var written = LocalSettings.SaveRenderWorkers(cut.ContentRoot, best);
                Console.WriteLine($"  Written to {written} as Cut:RenderWorkers. It applies from now on.");
            }
            else if (best != cut.Options.RenderWorkers)
            {
                Console.WriteLine($"  Re-run with --apply to save it - no config editing, and it applies from then on.");
            }
        }

        // Non-zero when something failed, so a CI step can gate on it.
        return report.AllPassed ? 0 : 4;
    }

    private static int Fonts(CupriCutService cut, CommandLine opts)
    {
        var composition = opts.Text("composition");
        if (string.IsNullOrWhiteSpace(composition))
        {
            using var bare = cut.OpenDocument(new Composition("(none)", cut.ContentRoot, "<div></div>", null, [], []));
            foreach (var family in bare.FontReport.RegisteredFamilies) Console.WriteLine(family);
            return 0;
        }

        var loaded = cut.LoadComposition(composition);
        using var doc = cut.OpenDocument(loaded);
        doc.Animate(0);
        using (doc.RenderToImage(cut.Options.DefaultWidth, cut.Options.DefaultHeight)) { }

        var report = doc.FontReport;
        Console.WriteLine(report.ToString());
        Console.WriteLine(report.IsDeterministic
            ? "\nDeterministic: every family resolved to a registered face."
            : "\nNOT deterministic: something resolved off this machine.");
        return report.IsDeterministic ? 0 : 3;
    }

    private static int Projects(CupriCutService cut)
    {
        var projects = cut.ListProjects();
        if (projects.Count == 0)
        {
            Console.WriteLine($"no projects under {cut.ProjectRoot}");
            return 0;
        }

        foreach (var relative in projects)
        {
            try
            {
                var project = cut.LoadProject(relative);
                Console.WriteLine($"{relative,-32} {project.Render.Width}x{project.Render.Height} @{project.Render.Fps:0.##}fps  {project.Render.Duration:0.###}s  {project.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{relative,-32} unreadable: {ex.Message}");
            }
        }
        return 0;
    }

    private static int Project(CupriCutService cut, CommandLine opts)
    {
        // The whole file, so a CI job can pipe it into jq exactly as an agent reads load_project.
        var name = opts.Text("name") ?? opts.Require("composition");
        Console.WriteLine(cut.LoadProject(name).ToJson());
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"cupricut: unknown verb '{verb}'.");
        Usage();
        return 1;
    }

    // ---- plumbing --------------------------------------------------------------------------

    private static SweepSpec Spec(CommandLine opts, double[] times, double? fps = null) => new()
    {
        Composition = opts.Require("composition"),
        Width = (int)opts.Number("width", 0),
        Height = (int)opts.Number("height", 0),
        Scale = (int)opts.Number("scale", 0),
        Times = times,
        SweepFps = fps ?? opts.Number("fps", 0),
        Background = opts.Text("background") is { Length: > 0 } c && SKColor.TryParse(c, out var colour) ? colour : null,
        Alpha = opts.Has("alpha") ? true : null,
        ShowBackground = opts.Has("no-background") ? false : null,
        OutputWidth = (int)opts.Number("output-width", 0),
        OutputHeight = (int)opts.Number("output-height", 0),
        Scaling = opts.Text("scaling") is { Length: > 0 } m ? Presentation.ParseMode(m) : null,
    };

    /// <summary>Always say what the requested length actually became. Silence when it was exact,
    /// the arithmetic and a suggestion when it was not.</summary>
    private static void ReportTiming(ClipPlan? plan)
    {
        if (plan is null) return;
        if (plan.Exact)
        {
            Console.WriteLine($"duration {plan.ActualSeconds:0.######}s exactly ({plan.Frames} frames at {plan.Fps:0.####} fps)");
            return;
        }
        Console.WriteLine($"duration {plan.ActualSeconds:0.######}s, asked for {plan.RequestedSeconds:0.######}s ({plan.DeltaMs:+0.###;-0.###;0}ms)");
        if (plan.Note is { Length: > 0 }) Console.WriteLine($"  {plan.Note}");
    }

    private static void Report(SweepReport report)
    {
        Console.WriteLine(
            $"swept {report.StepsRendered} frames to t={report.LastTime:0.###}s at {report.SweepFps:0.##} fps " +
            $"in {report.ElapsedMs:0}ms ({report.ElapsedMs / Math.Max(1, report.StepsRendered):0.0} ms/frame), kept {report.FramesKept}");
        foreach (var problem in report.FontProblems) Console.Error.WriteLine($"font: {problem}");
    }

    /// <summary>A file stem from a composition name. "hero.cut.json" is a two-part extension, so
    /// one GetFileNameWithoutExtension would leave "hero.cut".</summary>
    private static string ProjectStem(string name)
    {
        var file = Path.GetFileName(name);
        return CutProject.IsProjectPath(file)
            ? file[..^CutProject.Extension.Length]
            : Path.GetFileNameWithoutExtension(file);
    }

    private static AlphaMode ParseAlphaMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AlphaMode.Embedded;
        return value.Replace("-", "").Replace("_", "").ToLowerInvariant() switch
        {
            "embedded" or "channel" or "alpha" => AlphaMode.Embedded,
            "mattebelow" or "matte" or "vstack" or "stacked" or "below" => AlphaMode.MatteBelow,
            "matteright" or "hstack" or "sidebyside" or "right" => AlphaMode.MatteRight,
            _ => throw new ArgumentException($"Unknown --alpha-mode '{value}'. Use embedded, matteBelow or matteRight."),
        };
    }

    private static double[] Range(double from, double to, double fps)
    {
        var count = (int)Math.Floor((to - from) * fps + 1e-9);
        return [.. Enumerable.Range(0, count + 1).Select(i => Math.Round(from + i / fps, 6, MidpointRounding.AwayFromZero))];
    }

    private static double[] Sample(double duration, double? every, int count)
    {
        if (every is { } step && step > 0)
            return [.. Enumerable.Range(0, (int)Math.Floor(duration / step + 1e-9) + 1).Select(i => Math.Round(i * step, 6))];
        if (count <= 1) return [0];
        return [.. Enumerable.Range(0, count).Select(i => Math.Round(i * duration / (count - 1), 6))];
    }

    private static SKBitmap Thumbnail(SKBitmap source, int width)
    {
        if (width <= 0 || source.Width <= width) return source;
        var height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
        var scaled = source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (scaled is null) return source;
        source.Dispose();
        return scaled;
    }

    /// <summary>The same configuration layering the server does, minus the web host: the CLI reads
    /// <c>CupriCut.json</c> next to the executable so both agree about roots and limits.</summary>
    private static (CupriCutService Cut, VideoEncoder Encoder) Build(CommandLine opts)
    {
        var contentRoot = opts.Text("content-root") ?? Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("CupriCut.json", optional: true)
            .AddJsonFile("CupriCut.Local.json", optional: true)
            .AddEnvironmentVariables()
            .AddEnvironmentVariables(prefix: "CUPRICUT_")
            .Build();

        var cutOptions = configuration.GetSection(CutOptions.SectionName).Get<CutOptions>() ?? new CutOptions();
        if (opts.Text("compositions") is { Length: > 0 } roots)
            cutOptions.CompositionRoots = [.. roots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)];
        if (opts.Text("output") is { Length: > 0 } output) cutOptions.OutputRoot = output;
        if (opts.Text("ffmpeg") is { Length: > 0 } ffmpeg) cutOptions.FfmpegPath = ffmpeg;

        using var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(opts.Has("verbose") ? LogLevel.Information : LogLevel.Warning)
            .AddSimpleConsole(c => { c.SingleLine = true; c.TimestampFormat = null; }));

        var monitor = new StaticOptions<CutOptions>(cutOptions);
        var cut = new CupriCutService(monitor, loggerFactory.CreateLogger<CupriCutService>()) { ContentRoot = contentRoot };
        return (cut, new VideoEncoder(cut, loggerFactory.CreateLogger<VideoEncoder>()));
    }

    private static void Usage() => Console.WriteLine("""
        cupricut - write HTML, render video. No browser.

          cupricut frame  --composition <file> [--t 1.5] [--out frame.png]
          cupricut sheet  --composition <file> [--duration 3] [--count 9 | --every 0.25]
                                               [--columns 3] [--thumb 320] [--out sheet.png]
          cupricut frames --composition <file> [--duration 3 | --to 3] [--fps 30] [--out <dir>]
          cupricut video  --composition <file> [--duration 3 | --to 3] [--fps 30]
                                               [--codec h264|h265|vp9|prores|gif] [--out clip]
          cupricut export --composition <file> --formats mp4,mask,gif [--duration 3] [--alpha]
                                               [--fps 30] [--out <dir>]
          cupricut formats
          cupricut inspect --composition <file>
          cupricut lint    --composition <file> [--all]
          cupricut probe
          cupricut calibrate [--all] [--no-benchmark] [--apply]
          cupricut fonts  [--composition <file>]
          cupricut projects
          cupricut project --name <project>

        A .cut.json project works anywhere --composition takes an .html file, and supplies every
        argument you leave out - size, fps, duration, background, codec.

        Shared options
          --width N --height N       layout viewport in CSS pixels (default 1280x720)
          --scale N                  pixel multiplier, like a HiDPI display
          --output-width N           render into a frame of this size instead of the layout
          --output-height N          size. Name one and the other follows from the aspect.
                                     Not combinable with --scale: they say the same thing.
          --scaling <mode>           how the layout becomes that frame when they differ:
                                     fit (scale the layout to fit, letterboxed - the default,
                                     and what "4K the way it looks at 1080p" means), responsive
                                     (lay out at the output size, reflows), fixed (1:1 centred),
                                     hybrid (zoom the tight axis, reflow the long one),
                                     adaptive (hybrid above the layout size, responsive below)
          --background '#101014'     frame clear colour
          --alpha                    transparent clear
          --no-background            hide elements marked --cupricut-background, so the same
                                     composition gives an opaque clip and a transparent one
          --alpha-mode <mode>        embedded (real alpha channel; vp9/prores only),
                                     matteBelow or matteRight (alpha as a second image in
                                     the same frame - works with h264 and everything else)
          --compositions <dirs>      override Cut:CompositionRoots
          --output <dir>             override Cut:OutputRoot
          --ffmpeg <path>            override Cut:FfmpegPath
          --content-root <dir>       where CupriCut.json, fonts/ and compositions/ live
          --verbose                  log the sweep, and print stack traces

        --duration is a LENGTH: duration x fps frames exactly, so --duration 10 --fps 120 is a
        1200-frame, 10.000s clip. --to is an INCLUSIVE endpoint and keeps the frame at t=to, which
        is one frame more. Give one or the other, never both.

        Every verb sweeps from t=0. A frame is a function of t AND the frames before it, so the
        frame at t is the last of a sweep - which is the frame the video contains.
        """);
}

/// <summary>An <c>IOptionsMonitor</c> over one fixed value. The CLI has no configuration reload to
/// react to, and the service only asks for the current value.</summary>
internal sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;
    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>A very small <c>--flag value</c> parser. Enough for six verbs, and no dependency.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CommandLine Parse(ReadOnlySpan<string> args)
    {
        var parsed = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                // A bare first argument is the composition, so "cupricut frame hero.html" works.
                parsed._values.TryAdd("composition", arg);
                continue;
            }

            var name = arg[2..];
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                parsed._values[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            var next = i + 1 < args.Length ? args[i + 1] : null;
            if (next is not null && !next.StartsWith("--", StringComparison.Ordinal))
            {
                parsed._values[name] = next;
                i++;
            }
            else
            {
                parsed._values[name] = null;
            }
        }
        return parsed;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Text(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string Require(string name) =>
        Text(name) ?? throw new ArgumentException($"--{name} is required.");

    public double Number(string name, double fallback) =>
        Text(name) is { Length: > 0 } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    public double[] Numbers(string name) =>
        Text(name) is { Length: > 0 } text
            ? [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture))]
            : [];
}
