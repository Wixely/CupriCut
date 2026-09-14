using SkiaSharp;

namespace CupriCut.Services;

/// <summary>One render request, in the only terms the renderer has: a composition, a viewport, and
/// the times to keep.</summary>
public sealed record SweepSpec
{
    public required string Composition { get; init; }

    /// <summary>Layout viewport width in CSS pixels. Output is this times <see cref="Scale"/>.
    /// Zero means unset - a project's own width, or the configured default.</summary>
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Pixel multiplier, the way a HiDPI host works: lay out at the logical size and paint
    /// into a surface of this many times the pixels, canvas scaled. Not a bigger viewport - that
    /// would be a different composition, not a sharper one. Zero means unset.</summary>
    public int Scale { get; init; }

    /// <summary>The times to hand back, in seconds. Everything between 0 and the last of them is
    /// still rendered; these are the frames that are kept.</summary>
    public required IReadOnlyList<double> Times { get; init; }

    /// <summary>Steps per second the sweep advances at. This is what "the frame at t" means.
    /// Zero means unset.</summary>
    public double SweepFps { get; init; }

    /// <summary>Background the frame is cleared to. Null is unset: a project's own background, or
    /// white.</summary>
    public SKColor? Background { get; init; }

    /// <summary>Clear to transparent and keep the alpha channel. Null is unset.</summary>
    public bool? Alpha { get; init; }

    /// <summary>Sweep every intermediate frame even when the composition does not need it. The
    /// analysis is conservative, so this is for proving a difference rather than for safety.</summary>
    public bool ForceSweep { get; init; }
}

/// <summary>A frame the sweep was asked for. The image is owned by the sweep and disposed as soon
/// as the sink returns - encode it or copy it, do not keep it.</summary>
public sealed record SweptFrame(int Index, double Time, SKImage Image);

/// <summary>What a sweep cost and what it found, for the text half of a tool's answer.</summary>
public sealed record SweepReport(
    string Composition,
    int Width,
    int Height,
    int Scale,
    int StepsRendered,
    int FramesKept,
    double SweepFps,
    double LastTime,
    double ElapsedMs,
    bool Settled,
    IReadOnlyList<string> FontProblems)
{
    /// <summary>Whether the composition needed sweeping at all.</summary>
    public PurityVerdict Purity { get; init; } = PurityVerdict.Pure;

    /// <summary>How many threads rendered it. One unless the frames were independent and there were
    /// enough of them to be worth sharding.</summary>
    public int Workers { get; init; } = 1;

    /// <summary>Frames rendered purely to carry state to the ones that were asked for. Zero when the
    /// composition is pure in <c>t</c>.</summary>
    public int FramesDiscarded => Math.Max(0, StepsRendered - FramesKept);
}
