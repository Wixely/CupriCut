using CupriCut.Configuration;
using CupriFace;
using SkiaSharp;

namespace CupriCut.Services;

/// <summary>
/// How a composition's DESIGN size becomes an OUTPUT frame when the two differ.
///
/// <para>The question this answers is "render this at 4K the way it looks at 1080p" - which is not
/// the same request as "render this at 4K", and the difference is the whole point. Laying a
/// 1280x720 design out in a 3840-wide viewport reflows it: everything stays the same number of CSS
/// pixels and the composition simply has more room, so a centred card is a small card lost in a big
/// frame. Scaling it instead keeps the composition and makes it bigger.</para>
/// </summary>
public enum ScalingMode
{
    /// <summary>
    /// Lay out at the OUTPUT size, scale 1 - the document reflows like a web page.
    ///
    /// <para>What you want when the composition was written to fill whatever it is given, and the
    /// only honest choice when the output is a shape the design never anticipated.</para>
    /// </summary>
    Responsive,

    /// <summary>The design at 1:1, centred, with the clear colour around it. No scaling at all, so
    /// text is exactly as crisp as it was authored and a bigger output is just more margin.</summary>
    Fixed,

    /// <summary>
    /// The design scaled uniformly until it fits, centred, letterboxed if the aspects differ.
    /// <b>The default, and the answer to "4K the way it looks at 1080p".</b>
    ///
    /// <para>The engine has no equivalent, and deliberately: a WINDOW host never letterboxes - it
    /// reflows the loose axis instead, because empty bars in an application are a bug. A frame of
    /// video is not a window. Its size is fixed by the format, the composition's aspect is a
    /// decision someone made, and bars are the correct way to hold both.</para>
    /// </summary>
    Fit,

    /// <summary>Zoom the tighter axis to the design size and reflow the longer one, so the frame is
    /// always filled. <see cref="PresentInfo.Hybrid"/>. Identical to <see cref="Fit"/> when the
    /// aspects match, which is most of the time; on a mismatch this spends the extra room on
    /// content rather than on bars.</summary>
    Hybrid,

    /// <summary>Hybrid above the design size, Responsive below it. <see cref="PresentInfo.Adaptive"/>.
    /// Spends surplus, never crushes - the mode for a design that must also survive being rendered
    /// smaller than it was drawn.</summary>
    Adaptive,
}

/// <summary>
/// The geometry of one render: how big the surface is, what the document lays out at, and the
/// transform between them.
///
/// <para><b>Why this is one type.</b> The sweep, the parallel renderer and the video encoder each
/// worked out the frame size for themselves from the same three fallbacks, and the encoder has to
/// agree with the renderer EXACTLY - it tells ffmpeg the frame size up front, and a disagreement
/// is a pipe full of misaligned bytes rather than an error. Working the same thing out in three
/// places is how that goes wrong; it is worked out here.</para>
/// </summary>
/// <param name="OutputWidth">The surface, in real pixels. What ffmpeg is told, and what the PNG is.</param>
/// <param name="OutputHeight">The surface, in real pixels.</param>
/// <param name="LogicalWidth">The viewport the document lays out in, in CSS pixels.</param>
/// <param name="LogicalHeight">The viewport the document lays out in, in CSS pixels.</param>
/// <param name="Scale">What the canvas is scaled by before the document paints.</param>
/// <param name="OffsetX">Left inset of the scaled content, for the modes that do not fill.</param>
/// <param name="OffsetY">Top inset of the scaled content, for the modes that do not fill.</param>
/// <param name="Mode">Which rule produced this.</param>
public sealed record Presentation(
    int OutputWidth,
    int OutputHeight,
    float LogicalWidth,
    float LogicalHeight,
    float Scale,
    float OffsetX,
    float OffsetY,
    ScalingMode Mode)
{
    public long Pixels => (long)OutputWidth * OutputHeight;

    /// <summary>Raw RGBA bytes in one frame. What the pipe is sized against.</summary>
    public long FrameBytes => Pixels * 4;

    /// <summary>Whether the scaled content covers the whole frame. False means the clear colour
    /// shows around it, which is a thing worth telling a caller who asked for an opaque render and
    /// got bars.</summary>
    public bool Fills => OffsetX <= 0.5f && OffsetY <= 0.5f;

    /// <summary>Put the canvas into the document's coordinates. Paired with a
    /// <see cref="SKCanvas.Restore"/> by the caller, which also owns the clear.</summary>
    public void Apply(SKCanvas canvas)
    {
        canvas.Save();
        if (OffsetX != 0 || OffsetY != 0) canvas.Translate(OffsetX, OffsetY);
        if (Scale != 1) canvas.Scale(Scale);
    }

    /// <summary>The frame size, and nothing else. What a caller reads to find out how big the
    /// picture is, so it stays parseable - the story of how it got that way is <see cref="Detail"/>.</summary>
    public string Size => $"{OutputWidth}x{OutputHeight}";

    /// <summary>Prose, for a log line.</summary>
    public string Describe() =>
        IsPlain ? Size : $"{Size} from a {LogicalWidth:0.##}x{LogicalHeight:0.##} layout at {Scale:0.####}x ({Mode.ToString().ToLowerInvariant()})";

    /// <summary>Nothing happened between the layout and the frame: same size, no scale, no bars.</summary>
    private bool IsPlain =>
        Scale == 1 && OffsetX == 0 && OffsetY == 0
        && LogicalWidth == OutputWidth && LogicalHeight == OutputHeight;

    /// <summary>How the layout became the frame, or null when it simply WAS the frame - an answer
    /// should not grow a block explaining that nothing happened.</summary>
    public object? Detail() => IsPlain ? null : new
    {
        layout = $"{LogicalWidth:0.##}x{LogicalHeight:0.##}",
        scale = Math.Round(Scale, 4),
        mode = Mode.ToString().ToLowerInvariant(),
        // Only when there are bars, and then say how thick - someone who asked for an opaque render
        // and got them needs told rather than left to spot it.
        letterbox = Fills ? null : $"{OffsetX:0.#}px left and right, {OffsetY:0.#}px top and bottom",
    };

    /// <summary>
    /// Work out the geometry, resolving every setting the way everything else here does: what the
    /// caller asked for, then what the project remembers, then the server's configuration.
    /// </summary>
    public static Presentation Resolve(SweepSpec spec, RenderSettings? defaults, CutOptions options)
    {
        var designWidth = spec.Width > 0 ? spec.Width : defaults?.Width > 0 ? defaults.Width : options.DefaultWidth;
        var designHeight = spec.Height > 0 ? spec.Height : defaults?.Height > 0 ? defaults.Height : options.DefaultHeight;
        var scale = spec.Scale > 0 ? spec.Scale : defaults?.Scale > 0 ? defaults.Scale : 1;
        var outputWidth = spec.OutputWidth > 0 ? spec.OutputWidth : defaults?.OutputWidth ?? 0;
        var outputHeight = spec.OutputHeight > 0 ? spec.OutputHeight : defaults?.OutputHeight ?? 0;
        var mode = spec.Scaling ?? defaults?.Scaling ?? ScalingMode.Fit;

        // No output size named: the pixel multiplier is the whole story, and this is byte-for-byte
        // what CupriCut did before there were modes at all. scale:2 on a 1280x720 design is a
        // 2560x1440 frame of the same layout, which is Fit with the aspect matching exactly.
        if (outputWidth <= 0 && outputHeight <= 0)
            return new Presentation(designWidth * scale, designHeight * scale,
                designWidth, designHeight, scale, 0, 0, ScalingMode.Fit);

        // One of the two given: the other follows from the design's aspect, so "render it 4K wide"
        // is a complete request.
        if (outputWidth <= 0) outputWidth = (int)Math.Round(outputHeight * (designWidth / (double)designHeight));
        if (outputHeight <= 0) outputHeight = (int)Math.Round(outputWidth * (designHeight / (double)designWidth));

        // Both a multiplier and an output size is two answers to one question, and quietly picking
        // one is how someone gets a frame they did not ask for. Same rule as duration and to.
        if (scale > 1)
            throw new ArgumentException(
                $"Both scale ({scale}) and an output size ({outputWidth}x{outputHeight}) were given, and they are two ways of saying the same thing. " +
                $"Keep the output size - it says what you want - or drop it and let scale {scale} make a {designWidth * scale}x{designHeight * scale} frame.",
                nameof(spec));

        var (logicalWidth, logicalHeight, factor) = mode switch
        {
            ScalingMode.Responsive => Of(PresentInfo.Responsive(outputWidth, outputHeight)),
            ScalingMode.Fixed => Of(PresentInfo.Fixed(designWidth, designHeight)),
            ScalingMode.Hybrid => Of(PresentInfo.Hybrid(outputWidth, outputHeight, designWidth, designHeight)),
            ScalingMode.Adaptive => Of(PresentInfo.Adaptive(outputWidth, outputHeight, designWidth, designHeight)),

            // Fit is ours. PresentInfo.Zoom(w, h, f) derives the LOGICAL size from the window, so it
            // fills by construction and reflows the loose axis - which is right for a window and
            // wrong for a frame. Here the design keeps its shape and the frame gets bars.
            _ => (designWidth, designHeight,
                  Math.Min(outputWidth / (float)designWidth, outputHeight / (float)designHeight)),
        };

        // Only Fit and Fixed can leave room; the rest fill by construction, and half a pixel of
        // rounding is not an inset worth centring.
        var offsetX = Math.Max(0, (outputWidth - logicalWidth * factor) / 2);
        var offsetY = Math.Max(0, (outputHeight - logicalHeight * factor) / 2);

        return new Presentation(outputWidth, outputHeight, logicalWidth, logicalHeight, factor, offsetX, offsetY, mode);
    }

    private static (float Width, float Height, float Scale) Of(PresentInfo p) =>
        (p.LogicalWidth, p.LogicalHeight, p.Scale);

    /// <summary>The mode a caller named. Spelled forgivingly, the way the alpha modes are.</summary>
    public static ScalingMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ScalingMode.Fit;
        return value.Replace("-", "").Replace("_", "").Replace(" ", "").Trim().ToLowerInvariant() switch
        {
            "responsive" or "reflow" or "fluid" => ScalingMode.Responsive,
            "fixed" or "none" or "actual" => ScalingMode.Fixed,
            "fit" or "zoom" or "contain" or "letterbox" or "scale" => ScalingMode.Fit,
            "hybrid" or "hybridzoom" => ScalingMode.Hybrid,
            "adaptive" => ScalingMode.Adaptive,
            _ => throw new ArgumentException(
                $"Unknown scaling mode '{value}'. Use responsive, fixed, fit, hybrid or adaptive.", nameof(value)),
        };
    }
}
