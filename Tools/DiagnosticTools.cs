using System.ComponentModel;
using System.Text.Json;
using CupriCut.Services;
using CupriFace.Text;
using ModelContextProtocol.Server;

namespace CupriCut.Tools;

/// <summary>What this installation can do, and what a composition's text resolved to. Neither of
/// these renders anything an author asked for, which is exactly why they are cheap to call first.</summary>
[McpServerToolType]
public static class DiagnosticTools
{
    [McpServerTool(Name = "probe"),
     Description("""
        Report what this CupriCut can do: whether ffmpeg answers and which codecs it has, where
        compositions are read from and output is written to, and the limits in force. Call this
        before a first render_video - the release zips do not carry ffmpeg, only the Docker image
        does.
        """)]
    public static string Probe(CupriCutService cut, VideoEncoder encoder)
    {
        var options = cut.Options;
        var ffmpeg = options.EnableVideo ? encoder.Probe() : null;

        return JsonSerializer.Serialize(new
        {
            renderer = new
            {
                engine = $"CupriFace {typeof(CupriFace.CupriDocument).Assembly.GetName().Version}",
                skia = typeof(SkiaSharp.SKImage).Assembly.GetName().Version?.ToString(),
                backend = "CPU raster, headless - no browser and no window",
                deterministic = "Identical pixels on any machine of one OS; identical layout on all three. " +
                                "Skia's glyph rasteriser is DirectWrite on Windows, FreeType on Linux and CoreText on macOS, " +
                                "so the same face draws different pixels per OS. Not 'render anywhere, reproduce anywhere'.",
                fontPolicy = "RegisteredOnly, always",
            },
            video = new
            {
                enabled = options.EnableVideo,
                ffmpeg = ffmpeg is null ? null : new
                {
                    available = ffmpeg.Available,
                    path = ffmpeg.Path,
                    version = ffmpeg.Version,
                    codecs = ffmpeg.Encoders,
                    error = ffmpeg.Error,
                },
                known = VideoEncoder.Codecs.Values.Select(c => new { c.Name, c.Encoder, c.Extension, alpha = c.Alpha }),
            },
            paths = new
            {
                contentRoot = cut.ContentRoot,
                compositionRoots = cut.CompositionRoots,
                configuredRoots = options.CompositionRoots,
                outputRoot = cut.OutputRoot,
                fontDirectories = options.FontDirectories,
            },
            limits = new
            {
                maxFrames = options.MaxFrames,
                maxPixels = options.MaxPixels,
                maxInlineImageBytes = options.MaxInlineImageBytes,
                settleTimeoutSeconds = options.SettleTimeoutSeconds,
                ffmpegTimeoutSeconds = options.FfmpegTimeoutSeconds,
                defaults = new { options.DefaultWidth, options.DefaultHeight, options.DefaultFps },
            },
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "inspect"),
     Description("""
        What a composition IS: its timeline, its assets, the fonts it asks for and what answered.

        The timeline is the interesting half - every element carrying data-start / data-duration,
        when it is on screen, and which data-track it belongs to. Read this before changing timing,
        because it is the only way to see the whole sequence without rendering it.

        The composition is opened and rendered once to answer, because a layout is what asks for a
        font family - nothing resolves until something paints. Use lint for the verdict on whether
        it will render the same on another machine.
        """)]
    public static string Inspect(
        CupriCutService cut,
        [Description("Composition to look at: an HTML file or a .cut.json project, relative to a configured root.")] string composition)
    {
        var x = Inspector.Examine(cut, composition);

        return JsonSerializer.Serialize(new
        {
            composition = x.Composition,
            size = $"{x.Width}x{x.Height}",
            x.Fps,
            duration = x.Duration > 0 ? Math.Round(x.Duration, 6) : (double?)null,
            pureInTime = x.Purity.PureInTime,
            purity = x.Purity.Summary,

            // Null rather than an empty object when the composition has no timeline - most do not,
            // and a block saying so on every answer is noise.
            timeline = x.Timeline.Any ? new
            {
                duration = Math.Round(x.Timeline.Duration, 6),
                tracks = x.Timeline.Tracks,
                windows = x.Timeline.Windows.Select(w => new
                {
                    w.Index,
                    element = w.Describe(),
                    start = Math.Round(w.Start, 6),
                    end = double.IsPositiveInfinity(w.End) ? (double?)null : Math.Round(w.End, 6),
                    w.Track,
                    generatedClass = w.ClassName,
                }),
                problems = x.Timeline.Problems.Count == 0 ? null : x.Timeline.Problems,
            } : null,

            // Frame numbers are at the composition's OWN rate. A render at another rate writes
            // its own sidecar, because the same marks at 25 fps are different frames.
            events = x.Events.Any ? new
            {
                count = x.Events.Events.Count,
                names = x.Events.Names,
                marks = x.Events.Events.Select(e => new
                {
                    e.Name,
                    at = Math.Round(e.At, 6),
                    frame = e.Frame(x.Fps),
                    element = e.Describe(),
                    e.Track,
                    e.Relative,
                    pastTheEnd = x.Duration > 0 && e.At > x.Duration ? true : (bool?)null,
                }),
                problems = x.Events.Problems.Count == 0 ? null : x.Events.Problems,
            } : null,

            backdrop = x.Backdrops.Count == 0 ? null : new
            {
                marked = x.Backdrops.Count,
                elements = x.Backdrops.Select(b => b.Describe()),
            },

            assets = new
            {
                stylesheets = x.Stylesheets.Count == 0 ? null : x.Stylesheets,
                references = x.References.Count == 0 ? null : x.References,
            },

            fonts = new
            {
                registered = x.RegisteredFamilies,
                asked = x.Fonts.Select(f => new
                {
                    f.Asked,
                    f.Weight,
                    f.Slant,
                    f.Answered,
                    f.Source,
                    f.MachineDependent,
                }),
            },

            settled = x.Settled,
            pendingLoads = x.PendingLoads == 0 ? (int?)null : x.PendingLoads,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "lint"),
     Description("""
        Will this composition render the same on another machine, and does it render what its
        author meant?

        Three sources in one answer. The engine's own reader (CF* codes) catches the silent things -
        a tag that never closed, a component nothing registered, a CSS property it ignored, an
        element that laid out with no area while holding content. CupriCut adds what only it knows
        (CUT* codes): a font answered by the MACHINE rather than by a registered file, a resource
        that never arrived, a timeline it could not make sense of. And it reports whether the
        composition is pure in t - not a fault, but the difference between sampling any frame
        directly and sweeping every frame before it.

        verdict is "clean", "warnings" or "errors", which is what a CI step gates on.
        """)]
    public static string Lint(
        CupriCutService cut,
        [Description("Composition to check: an HTML file or a .cut.json project, relative to a configured root.")] string composition,
        [Description("Include the informational findings. Off by default, so the answer is what needs attention.")] bool all = false)
    {
        var x = Inspector.Examine(cut, composition);
        var shown = all ? x.Findings : [.. x.Findings.Where(f => f.Level != FindingLevel.Info)];

        return JsonSerializer.Serialize(new
        {
            composition = x.Composition,
            x.Verdict,
            x.Errors,
            x.Warnings,
            pureInTime = x.Purity.PureInTime,
            findings = shown.Select(f => new
            {
                level = f.Level.ToString().ToLowerInvariant(),
                f.Code,
                what = f.What,
                f.Fix,
                line = f.Line == 0 ? (int?)null : f.Line,
            }),
            note = all || x.Findings.Count == shown.Count
                ? null
                : $"{x.Findings.Count - shown.Count} informational finding(s) hidden. Pass all:true for them.",
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_fonts"),
     Description("""
        List the font families registered on this server, and - given a composition - what every
        family its stylesheet asked for actually resolved to.

        The font policy is RegisteredOnly and cannot be turned off, so a family no registered face
        covers is a hard error naming the family rather than a silent substitution. This is how you
        find out before a render does.
        """)]
    public static string ListFonts(
        CupriCutService cut,
        [Description("Optional composition to resolve against, relative to a configured composition root.")] string? composition = null)
    {
        if (string.IsNullOrWhiteSpace(composition))
        {
            // No document, so nothing has asked for a family yet: an empty page is enough to see
            // what the server's own font directories registered.
            using var bare = cut.OpenDocument(new Composition("(none)", cut.ContentRoot, "<div></div>", null, [], []));
            var bareReport = bare.FontReport;
            return JsonSerializer.Serialize(new
            {
                registered = bareReport.RegisteredFamilies,
                fontDirectories = cut.Options.FontDirectories,
                problems = bareReport.Problems.Select(p => new { p.Family, p.Reason, p.Sources }),
                note = "Pass a composition to see which family answered each request.",
            }, JsonOpts.Default);
        }

        var loaded = cut.LoadComposition(composition);
        using var doc = cut.OpenDocument(loaded);

        // A layout is what asks for a family, so nothing resolves until something renders.
        doc.Animate(0);
        using (doc.RenderToImage(cut.Options.DefaultWidth, cut.Options.DefaultHeight)) { }

        var report = doc.FontReport;
        return JsonSerializer.Serialize(new
        {
            composition = loaded.Path,
            registered = report.RegisteredFamilies,
            deterministic = report.IsDeterministic,
            resolutions = report.Resolutions.Select(r => new
            {
                asked = r.Family,
                r.Weight,
                slant = r.Slant.ToString(),
                answered = r.ResolvedFamily,
                source = r.Source.ToString(),
                machineDependent = r.Source != FontSource.Registered,
            }),
            problems = report.Problems.Select(p => new { p.Family, p.Reason, p.Sources }),
            stylesheets = loaded.Stylesheets,
            references = loaded.References.Distinct(),
        }, JsonOpts.Default);
    }
}
