using System.ComponentModel;
using System.Text.Json;
using CupriCut.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace CupriCut.Tools;

/// <summary>
/// The tools an agent iterates with.
///
/// <para>The order here is the order they matter in. An agent's loop is look, adjust, look, so the
/// first tool returns a picture and the second returns a whole motion as one picture. The file
/// tools come after, because a PNG sequence and an MP4 are what a build step wants, not what an
/// author wants.</para>
/// </summary>
[McpServerToolType]
public static class RenderTools
{
    [McpServerTool(Name = "render_frame"),
     Description("""
        Render one frame of a composition and return it as an image.

        The frame is produced by sweeping from t=0 to the requested time, not by jumping there: a
        frame is a function of t AND the frames before it, because transitions interpolate from
        what the previous frame held. Sweeping costs about 5.6 ms per frame of sweep (so ~0.5 s to
        preview t=3 at 30 fps) and is the only reading of "the frame at t" that matches the video.
        """)]
    public static CallToolResult RenderFrame(
        CupriCutService cut,
        [Description("Composition to render: an HTML file, relative to a configured composition root.")] string composition,
        [Description("Time in seconds. The sweep still starts at 0 and renders every frame up to this one.")] double t = 0,
        [Description("Layout viewport width in CSS pixels.")] int width = 0,
        [Description("Layout viewport height in CSS pixels.")] int height = 0,
        [Description("Pixel multiplier, like a HiDPI display: laid out at width x height, painted at that times scale. Defaults to a project's own scale, or 1.")] int scale = 0,
        [Description("Render into a frame of this width instead of the layout width. Naming one of outputWidth/outputHeight is enough - the other follows from the layout aspect. Cannot be combined with scale.")] int outputWidth = 0,
        [Description("Render into a frame of this height instead of the layout height.")] int outputHeight = 0,
        [Description("How the layout becomes the output frame when they differ: 'fit' (scale the layout to fit, letterboxed - the default, and what '4K the way it looks at 1080p' means), 'responsive' (lay out at the output size, reflows), 'fixed' (1:1, centred), 'hybrid' (zoom the tight axis, reflow the long one), 'adaptive' (hybrid above the layout size, responsive below).")] string? scaling = null,
        [Description("Frames per second the sweep steps at. This is what 'the frame at t' means.")] double fps = 0,
        [Description("Background colour, e.g. '#101014'. Default white.")] string? background = null,
        [Description("Clear to transparent and keep the alpha channel.")] bool? alpha = null,
        [Description("Draw the elements marked --cupricut-background. False renders the same composition over nothing, which is what an edit wants; a composition that marks nothing is unaffected. Defaults to the project's own choice, or true.")] bool? showBackground = null,
        [Description("Also write the PNG here, relative to the output root.")] string? save = null)
    {
        var loaded = cut.LoadComposition(composition);
        byte[]? png = null;
        var report = cut.Sweep(
            loaded,
            new SweepSpec
            {
                Composition = composition,
                Width = width,
                Height = height,
                Scale = scale,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Scaling = scaling is null ? null : Presentation.ParseMode(scaling),
                Times = [t],
                SweepFps = fps,
                Background = ToolSupport.ParseColor(background),
                Alpha = alpha,
                ShowBackground = showBackground,
            },
            frame => png = FrameEncoder.Encode(frame.Image));

        if (png is null) throw new InvalidOperationException($"The sweep produced no frame at t={ToolSupport.Seconds(t)}.");

        string? saved = null;
        if (!string.IsNullOrWhiteSpace(save))
        {
            saved = cut.ResolveWrite(save);
            File.WriteAllBytes(saved, png);
        }

        return Picture(cut, png, saved, $"{Path.GetFileName(report.Composition)} at t = {ToolSupport.Seconds(t)}s", new
        {
            composition = report.Composition,
            t,
            saved,
            bytes = png.Length,
            backdrop = ToolSupport.Backdrop(loaded, showBackground),
            cost = ToolSupport.Cost(report),
        });
    }

    [McpServerTool(Name = "contact_sheet"),
     Description("""
        Render several times of a composition and return them tiled into a single timestamped image.

        The highest-value tool here: one picture shows a whole motion, so judging timing costs one
        image instead of thirty. Each cell is rendered at the real frame size and then thumbnailed,
        so what you see is what the video contains rather than what the composition does in a small
        viewport. All the cells come out of one sweep.
        """)]
    public static CallToolResult ContactSheet(
        CupriCutService cut,
        [Description("Composition to render: an HTML file, relative to a configured composition root.")] string composition,
        [Description("Explicit times in seconds. Overrides duration/every/count.")] double[]? times = null,
        [Description("Length of the motion in seconds, sampled by every or count.")] double? duration = null,
        [Description("Sample every N seconds across the duration.")] double? every = null,
        [Description("Sample this many evenly spaced times across the duration. Default 9.")] int? count = null,
        [Description("Cells per row. Default is a roughly square grid.")] int columns = 0,
        [Description("Layout viewport width in CSS pixels - the real frame size, not the cell size.")] int width = 0,
        [Description("Layout viewport height in CSS pixels.")] int height = 0,
        [Description("Width each cell is thumbnailed to. Default 320.")] int thumbnailWidth = 320,
        [Description("Frames per second the sweep steps at.")] double fps = 0,
        [Description("Background colour, e.g. '#101014'. Default white.")] string? background = null,
        [Description("Clear to transparent; cells get a checkerboard so transparency reads as transparency.")] bool? alpha = null,
        [Description("Draw the elements marked --cupricut-background. False renders the same composition over nothing, which is what an edit wants; a composition that marks nothing is unaffected. Defaults to the project's own choice, or true.")] bool? showBackground = null,
        [Description("Also write the sheet here, relative to the output root.")] string? save = null)
    {
        var loaded = cut.LoadComposition(composition);
        // A project knows how long it runs, so a sheet of one needs no duration argument at all.
        var span = duration ?? loaded.Defaults?.Duration;
        var sample = ToolSupport.SampleTimes(times, every, span, count);
        var transparent = alpha ?? loaded.Defaults?.Alpha ?? false;
        var cells = new List<(SKBitmap Frame, double Time)>(sample.Length);

        SweepReport report;
        try
        {
            report = cut.Sweep(
                loaded,
                new SweepSpec
                {
                    Composition = composition,
                    Width = width,
                    Height = height,
                    Scale = 1,
                    Times = sample,
                    SweepFps = fps,
                    Background = ToolSupport.ParseColor(background),
                    Alpha = alpha,
                    ShowBackground = showBackground,
                },
                frame => cells.Add((ToolSupport.Thumbnail(FrameEncoder.Copy(frame.Image), thumbnailWidth), frame.Time)));
        }
        catch
        {
            foreach (var (frame, _) in cells) frame.Dispose();
            throw;
        }

        var grid = columns > 0 ? columns : (int)Math.Ceiling(Math.Sqrt(cells.Count));
        var png = Services.ContactSheet.Compose(cells, grid, transparent);

        string? saved = null;
        if (!string.IsNullOrWhiteSpace(save))
        {
            saved = cut.ResolveWrite(save);
            File.WriteAllBytes(saved, png);
        }

        return Picture(cut, png, saved,
            $"{Path.GetFileName(report.Composition)}, {sample.Length} frames from t = {ToolSupport.Seconds(sample[0])}s to t = {ToolSupport.Seconds(sample[^1])}s",
            new
            {
                composition = report.Composition,
                times = sample,
                columns = grid,
                thumbnailWidth,
                saved,
                bytes = png.Length,
                backdrop = ToolSupport.Backdrop(loaded, showBackground),
            cost = ToolSupport.Cost(report),
            });
    }

    [McpServerTool(Name = "render_frames"),
     Description("""
        Render a range of a composition to a PNG sequence on disk and return the file list.

        This is the slow path on purpose: PNG encoding costs around 45 ms a frame against the
        render's 5.6, so the encoder is the cost here, not the renderer. The encodes run on the
        thread pool. If you want a video, use render_video - it pipes raw pixels and never encodes
        a PNG at all.
        """)]
    public static string RenderFrames(
        CupriCutService cut,
        [Description("Composition to render: an HTML file, relative to a configured composition root.")] string composition,
        [Description("First second to keep. The sweep always starts at 0 regardless.")] double from = 0,
        [Description("Last second to keep. Defaults to a project's own duration, or 1 second.")] double to = 0,
        [Description("Frames per second.")] double fps = 0,
        [Description("Layout viewport width in CSS pixels.")] int width = 0,
        [Description("Layout viewport height in CSS pixels.")] int height = 0,
        [Description("Pixel multiplier, like a HiDPI display. Defaults to a project's own scale, or 1.")] int scale = 0,
        [Description("Render into a frame of this width instead of the layout width. Naming one of outputWidth/outputHeight is enough - the other follows from the layout aspect. Cannot be combined with scale.")] int outputWidth = 0,
        [Description("Render into a frame of this height instead of the layout height.")] int outputHeight = 0,
        [Description("How the layout becomes the output frame when they differ: 'fit' (scale the layout to fit, letterboxed - the default, and what '4K the way it looks at 1080p' means), 'responsive' (lay out at the output size, reflows), 'fixed' (1:1, centred), 'hybrid' (zoom the tight axis, reflow the long one), 'adaptive' (hybrid above the layout size, responsive below).")] string? scaling = null,
        [Description("Background colour, e.g. '#101014'. Default white.")] string? background = null,
        [Description("Clear to transparent and keep the alpha channel.")] bool? alpha = null,
        [Description("Draw the elements marked --cupricut-background. False renders the same composition over nothing, which is what an edit wants; a composition that marks nothing is unaffected. Defaults to the project's own choice, or true.")] bool? showBackground = null,
        [Description("Directory under the output root to write into. Default is the composition's name.")] string? outputDirectory = null)
    {
        var loaded = cut.LoadComposition(composition);
        var rate = fps > 0 ? fps : loaded.Defaults?.Fps > 0 ? loaded.Defaults.Fps : cut.Options.DefaultFps;
        // An explicit `to` is an inclusive endpoint; a project's duration is a length, so it yields
        // exactly duration x fps files - the same count render_video would produce.
        double[] times;
        ClipPlan? plan = null;
        if (to > 0) times = ToolSupport.Range(from, to, rate);
        else (times, plan) = ClipPlanner.Plan(from, loaded.Defaults?.Duration ?? 1, rate);
        // A named directory is used exactly as named; an unnamed one is stamped, so today's run
        // does not quietly replace yesterday's.
        var stem = ToolSupport.SafeStem(outputDirectory ?? composition, "frames");
        var folder = OutputNaming.Explicit(outputDirectory) ? stem : OutputNaming.Run(stem);
        var directory = Path.GetDirectoryName(cut.ResolveWrite(Path.Combine(folder, ".keep")))!;

        var spec = new SweepSpec
        {
            Composition = composition,
            Width = width,
            Height = height,
            Scale = scale,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            Scaling = scaling is null ? null : Presentation.ParseMode(scaling),
            Times = times,
            SweepFps = rate,
            Background = ToolSupport.ParseColor(background),
            Alpha = alpha,
            ShowBackground = showBackground,
        };

        // Streamed, not collected: a long sequence must not hold every frame in memory. The writer
        // also applies back-pressure, so the sweep runs at the rate the encoders can keep up with.
        //
        // Deliberately NOT sharded across cores, though it looks like the obvious candidate.
        // Measured: sharding render+encode over six workers took 3.55s against 2.75s for this
        // path, because this one already runs the PNG encodes on the whole thread pool while the
        // sweep renders - which is the better balance. Parallelism helps where the encoder is not
        // already saturating the machine; here it is.
        using var writer = new FrameSequenceWriter();
        var report = cut.Sweep(loaded, spec,
            frame => writer.Add(frame.Image, Path.Combine(directory, ToolSupport.FrameName(stem, frame.Index, frame.Time))));
        var written = writer.Complete();

        return JsonSerializer.Serialize(new
        {
            composition = report.Composition,
            directory,
            fps = rate,
            from,
            to = times[^1],
            seconds = Math.Round(times.Length / rate, 6),
            frames = written.Length,
            timing = Timing(plan),
            // A ten-minute sequence is tens of thousands of names; listing them all would bury the
            // answer. Past a readable handful, say where they are and what they are called.
            files = written.Length <= 40 ? written.Select(Path.GetFileName) : null,
            first = Path.GetFileName(written[0]),
            last = Path.GetFileName(written[^1]),
            events = ToolSupport.Events(loaded, directory, stem, rate, times.Length / rate),
            backdrop = ToolSupport.Backdrop(loaded, showBackground),
            cost = ToolSupport.Cost(report),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "render_video"),
     Description("""
        Render a range of a composition straight into a video file.

        Raw RGBA goes down ffmpeg's stdin as each frame comes off the sweep, so no PNG is ever
        encoded on this path - 3 seconds at 30 fps takes well under a second. alpha:true selects a
        transparent clear and a codec that carries an alpha channel, because h264 does not and a
        silently opaque 'transparent' video is the worse outcome. Call probe first if you are not
        sure ffmpeg is here.
        """)]
    public static string RenderVideo(
        CupriCutService cut,
        VideoEncoder encoder,
        [Description("Composition to render: an HTML file, relative to a configured composition root.")] string composition,
        [Description("First second to keep. The sweep always starts at 0 regardless.")] double from = 0,
        [Description("Clip length in seconds. duration x fps frames exactly, so 10 at 120 fps is a 1200-frame, 10.000s file. Defaults to a project's own duration.")] double duration = 0,
        [Description("Alternative to duration: an INCLUSIVE last second to keep, which is one frame longer than the same number as a duration.")] double to = 0,
        [Description("Frames per second, for both the sweep and the output.")] double fps = 0,
        [Description("Layout viewport width in CSS pixels.")] int width = 0,
        [Description("Layout viewport height in CSS pixels.")] int height = 0,
        [Description("Pixel multiplier, like a HiDPI display. Defaults to a project's own scale, or 1.")] int scale = 0,
        [Description("Render into a frame of this width instead of the layout width. Naming one of outputWidth/outputHeight is enough - the other follows from the layout aspect. Cannot be combined with scale.")] int outputWidth = 0,
        [Description("Render into a frame of this height instead of the layout height.")] int outputHeight = 0,
        [Description("How the layout becomes the output frame when they differ: 'fit' (scale the layout to fit, letterboxed - the default, and what '4K the way it looks at 1080p' means), 'responsive' (lay out at the output size, reflows), 'fixed' (1:1, centred), 'hybrid' (zoom the tight axis, reflow the long one), 'adaptive' (hybrid above the layout size, responsive below).")] string? scaling = null,
        [Description("h264, h265, vp9, prores or gif. Default h264, or vp9 when alpha is set.")] string? codec = null,
        [Description("Transparent background.")] bool? alpha = null,
        [Description("How transparency is carried: 'embedded' (a real alpha channel - needs vp9 or prores), 'matteBelow' (colour on top, alpha greyscale below, doubles the height), 'matteRight' (side by side), or 'mask' (the alpha ALONE as a black-and-white clip, no colour). A matte or a mask works with ANY codec including h264, which has no alpha channel of its own.")] string? alphaMode = null,
        [Description("Background colour when alpha is false, e.g. '#101014'. Default white.")] string? background = null,
        [Description("Draw the elements marked --cupricut-background. False renders the same composition over nothing, which is what an edit wants; a composition that marks nothing is unaffected. Defaults to the project's own choice, or true.")] bool? showBackground = null,
        [Description("Render threads. 0 uses every core, which is the default; 1 forces the sequential path. Only a composition pure in t can be sharded - an impure one is swept in order whatever this says.")] int workers = 0,
        [Description("File name under the output root. The codec's own extension is used if none is given.")] string? output = null)
    {
        cut.EnsureVideoAllowed();

        var loaded = cut.LoadComposition(composition);
        var defaults = loaded.Defaults;
        var rate = fps > 0 ? fps : defaults?.Fps > 0 ? defaults.Fps : cut.Options.DefaultFps;
        var transparent = alpha ?? defaults?.Alpha ?? false;

        // A length and an endpoint are different questions. duration (and a project's own duration,
        // which is a length) gives exactly duration x fps frames; an explicit `to` is an endpoint
        // and is kept, which is one frame more. Naming both is ambiguous, so it is refused.
        if (duration > 0 && to > 0)
            throw new ArgumentException("Give either duration or to, not both - a length and an inclusive endpoint differ by one frame.", nameof(duration));

        var span = duration > 0 ? duration : to > 0 ? 0 : defaults?.Duration ?? 3;
        double[] times;
        ClipPlan? plan = null;
        if (span > 0) (times, plan) = ClipPlanner.Plan(from, span, rate);
        else times = ToolSupport.Range(from, to, rate);
        var mode = ParseAlphaMode(alphaMode);
        var picked = VideoEncoder.Resolve(codec ?? defaults?.Codec, transparent, mode);
        // An explicit name is honoured exactly - something is probably expecting it. A name
        // CupriCut chose carries the project and the moment, because renders are takes and takes
        // accumulate.
        var stem = ToolSupport.SafeStem(output ?? composition, "render");
        var file = OutputNaming.Explicit(output) ? stem + picked.Extension : OutputNaming.File(stem, picked.Extension);
        var path = cut.ResolveWrite(file);

        var (video, report) = encoder.EncodeFastest(
            loaded,
            new SweepSpec
            {
                Composition = composition,
                Width = width,
                Height = height,
                Scale = scale,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Scaling = scaling is null ? null : Presentation.ParseMode(scaling),
                Times = times,
                SweepFps = rate,
                Background = ToolSupport.ParseColor(background),
                Alpha = alpha,
                ShowBackground = showBackground,
            },
            path, picked, rate, mode, workers, cut.RenderLog);

        return JsonSerializer.Serialize(new
        {
            composition = report.Composition,
            video = video.Path,
            bytes = video.Bytes,
            frames = video.Frames,
            seconds = Math.Round(video.Seconds, 6),
            fps = video.Fps,
            codec = $"{picked.Name} ({picked.Encoder})",
            alpha = video.Alpha,
            alphaMode = video.Alpha ? video.AlphaMode.ToString() : null,
            pixelFormat = video.PixelFormat,
            note = video.Note,
            timing = Timing(plan),
            events = ToolSupport.Events(loaded, Path.GetDirectoryName(video.Path)!,
                Path.GetFileNameWithoutExtension(video.Path), video.Fps, video.Seconds),
            backdrop = ToolSupport.Backdrop(loaded, showBackground),
            cost = ToolSupport.Cost(report),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "export"),
     Description("""
        Render a composition once and write it out in every format asked for.

        One verb, a list of outcomes - mp4, h265, webm, mov, gif, mask, matte, frames - rather than
        a codec argument someone has to already understand. Each name carries the codec, the pixel
        format and the alpha handling that outcome needs.

        The formats share the render, and rendering is what a clip costs, so the second format is
        very nearly free: a frame written to four pipes costs no more to produce than a frame
        written to one. They therefore share the render's alpha too. With alpha:true a format that
        cannot carry an alpha channel - mp4, h265, gif - keeps the straight colour and goes black
        where nothing was drawn, which alongside 'mask' is exactly the colour-and-matte pair an
        editor keys. mask and matte are refused WITHOUT alpha, because the alpha channel is what
        they are made of.

        Call list_formats to see what each one gives you.
        """)]
    public static string Export(
        CupriCutService cut,
        VideoEncoder encoder,
        [Description("Composition to render: an HTML file or a .cut.json project, relative to a configured root.")] string composition,
        [Description("Formats to write: mp4, h265, webm, mov, gif, mask, matte, frames. Default mp4.")] string[]? formats = null,
        [Description("First second to keep. The sweep always starts at 0 regardless.")] double from = 0,
        [Description("Clip length in seconds. duration x fps frames exactly. Defaults to a project's own duration.")] double duration = 0,
        [Description("Alternative to duration: an INCLUSIVE last second to keep, which is one frame longer than the same number as a duration.")] double to = 0,
        [Description("Frames per second, for both the sweep and the output.")] double fps = 0,
        [Description("Layout viewport width in CSS pixels.")] int width = 0,
        [Description("Layout viewport height in CSS pixels.")] int height = 0,
        [Description("Pixel multiplier, like a HiDPI display. Defaults to a project's own scale, or 1.")] int scale = 0,
        [Description("Render into a frame of this width instead of the layout width. Naming one of outputWidth/outputHeight is enough - the other follows from the layout aspect. Cannot be combined with scale.")] int outputWidth = 0,
        [Description("Render into a frame of this height instead of the layout height.")] int outputHeight = 0,
        [Description("How the layout becomes the output frame when they differ: 'fit' (scale the layout to fit, letterboxed - the default, and what '4K the way it looks at 1080p' means), 'responsive' (lay out at the output size, reflows), 'fixed' (1:1, centred), 'hybrid' (zoom the tight axis, reflow the long one), 'adaptive' (hybrid above the layout size, responsive below).")] string? scaling = null,
        [Description("Transparent background. Required by mask and matte.")] bool? alpha = null,
        [Description("Background colour when alpha is false, e.g. '#101014'. Default white.")] string? background = null,
        [Description("Draw the elements marked --cupricut-background. False renders the same composition over nothing, which is what an edit wants.")] bool? showBackground = null,
        [Description("Render threads. 0 uses every core, which is the default; 1 forces the sequential path.")] int workers = 0,
        [Description("Directory under the output root to write into. Default is the composition's name.")] string? outputDirectory = null)
    {
        cut.EnsureVideoAllowed();

        var loaded = cut.LoadComposition(composition);
        var defaults = loaded.Defaults;
        var rate = fps > 0 ? fps : defaults?.Fps > 0 ? defaults.Fps : cut.Options.DefaultFps;

        if (duration > 0 && to > 0)
            throw new ArgumentException("Give either duration or to, not both - a length and an inclusive endpoint differ by one frame.", nameof(duration));

        var span = duration > 0 ? duration : to > 0 ? 0 : defaults?.Duration ?? 3;
        double[] times;
        ClipPlan? plan = null;
        if (span > 0) (times, plan) = ClipPlanner.Plan(from, span, rate);
        else times = ToolSupport.Range(from, to, rate);

        // One export is one thing that happens to have several files in it, so it gets one
        // folder. The moment goes on the FOLDER rather than on every file inside it - opening it
        // should show hero_mp4.mp4 and hero_mask.mp4, not the same timestamp four times.
        var stem = ToolSupport.SafeStem(outputDirectory ?? composition, "export");
        var folder = OutputNaming.Explicit(outputDirectory) ? stem : OutputNaming.Run(stem);
        var directory = Path.GetDirectoryName(cut.ResolveWrite(Path.Combine(folder, ".keep")))!;
        Directory.CreateDirectory(directory);

        var export = ExportFormats.Plan(formats ?? [], alpha, defaults?.Alpha ?? false,
            name => Path.Combine(directory, stem + "_" + name));

        var (videos, report) = encoder.ExportFastest(
            loaded,
            new SweepSpec
            {
                Composition = composition,
                Width = width,
                Height = height,
                Scale = scale,
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Scaling = scaling is null ? null : Presentation.ParseMode(scaling),
                Times = times,
                SweepFps = rate,
                Background = ToolSupport.ParseColor(background),
                // export.Alpha, not the raw argument: the targets were built against this, and a
                // render that disagreed with them would write a "mask" that was not one.
                Alpha = export.Alpha,
                ShowBackground = showBackground,
            },
            export.Targets, rate, workers, cut.RenderLog);

        return JsonSerializer.Serialize(new
        {
            composition = report.Composition,
            directory,
            alpha = export.Alpha,
            frames = videos[0].Frames,
            seconds = Math.Round(videos[0].Seconds, 6),
            fps = rate,
            files = export.Targets.Zip(videos, (target, video) => new
            {
                format = target.Name,
                path = video.Path,
                bytes = video.Bytes,
                codec = $"{video.Codec} ({target.Codec.Encoder})",
                pixelFormat = video.PixelFormat,
                note = video.Note,
            }),
            timing = Timing(plan),
            events = ToolSupport.Events(loaded, directory, stem, rate, videos[0].Seconds),
            backdrop = ToolSupport.Backdrop(loaded, showBackground),
            cost = ToolSupport.Cost(report),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_formats"),
     Description("Every format export can write, and what each one gives you.")]
    public static string ListFormats() =>
        JsonSerializer.Serialize(new { formats = ExportFormats.Describe() }, JsonOpts.Default);

    /// <summary>The alpha mode a caller named. Spelled forgivingly, because "matte-below" and
    /// "matteBelow" are the same request.</summary>
    private static AlphaMode ParseAlphaMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AlphaMode.Embedded;
        var cleaned = value.Replace("-", "").Replace("_", "").Replace(" ", "").Trim();
        return cleaned.ToLowerInvariant() switch
        {
            "embedded" or "channel" or "alpha" => AlphaMode.Embedded,
            "mattebelow" or "matte" or "vstack" or "stacked" or "below" => AlphaMode.MatteBelow,
            "matteright" or "hstack" or "sidebyside" or "right" => AlphaMode.MatteRight,
            "maskonly" or "mask" or "alphaonly" or "luma" => AlphaMode.MaskOnly,
            _ => throw new ArgumentException(
                $"Unknown alphaMode '{value}'. Use embedded, matteBelow, matteRight or mask.", nameof(value)),
        };
    }

    /// <summary>What a requested length actually became. Reported whenever a length was asked for,
    /// and carrying a note whenever it could not be honoured exactly - a clip is whole frames, so a
    /// request that is not a whole number of them has to move, and moving silently is how someone
    /// finds out much later that their 10s cut is 10.010s.</summary>
    private static object? Timing(ClipPlan? plan) => plan is null ? null : new
    {
        requestedSeconds = plan.RequestedSeconds,
        actualSeconds = Math.Round(plan.ActualSeconds, 6),
        plan.Frames,
        plan.Fps,
        plan.Exact,
        plan.DeltaMs,
        plan.Note,
    };

    /// <summary>An image and a line of prose about it - and, for a picture too big to be worth an
    /// agent's context, the path it was written to instead.</summary>
    private static CallToolResult Picture(CupriCutService cut, byte[] png, string? saved, string caption, object detail)
    {
        var json = JsonSerializer.Serialize(detail, JsonOpts.Default);

        if (png.Length > cut.Options.MaxInlineImageBytes)
        {
            // Spilling megabytes of base64 into a conversation helps nobody. Write it, say where,
            // and say what to ask for instead.
            saved ??= cut.ResolveWrite($"oversize-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
            if (!File.Exists(saved)) File.WriteAllBytes(saved, png);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = $"{caption} - {png.Length:N0} bytes, over Cut:MaxInlineImageBytes ({cut.Options.MaxInlineImageBytes:N0}), " +
                               $"so it was written to {saved} rather than returned inline. Ask for a smaller width, or a smaller scale, to see it here.\n{json}",
                    },
                ],
            };
        }

        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(png, "image/png"),
                new TextContentBlock { Text = $"{caption}\n{json}" },
            ],
        };
    }
}
