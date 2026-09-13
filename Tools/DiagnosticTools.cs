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
