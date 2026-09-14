using CupriCut.Services;
using CupriFace;
using SkiaSharp;

namespace CupriCut.Gui;

/// <summary>
/// One composition, kept open and rendered repeatedly.
///
/// <para><b>Why this exists.</b> Every preview used to load the composition, settle it, and throw it
/// away — about 160 ms of which roughly 5 ms was the actual frame. That is fine for a one-shot tool
/// call and hopeless for a scrub bar, where the cost lands on every drag position. Holding the
/// document open turns scrubbing into what it should be: one <c>Animate</c> and one paint.</para>
///
/// <para><b>Not thread-safe, deliberately.</b> A <c>CupriDocument</c> is single-threaded, so this is
/// owned by <see cref="StudioController"/>'s render thread and touched from nowhere else.</para>
///
/// <para>Purity decides how a time is reached. A composition that is pure in <c>t</c> is rendered
/// directly at any time in any order. One that is not has to be walked: forwards from where it
/// already is when the new time is later, and from zero when it is earlier, because state that was
/// carried forward cannot be carried back.</para>
/// </summary>
public sealed class PreviewSession : IDisposable
{
    private readonly CupriDocument _document;
    private readonly SKSurface _surface;
    private readonly SKColor _clear;
    private readonly double _stepFps;
    private double _at = double.NaN;

    private PreviewSession(CupriDocument document, SKSurface surface, string composition,
        int width, int height, SKColor clear, double stepFps, PurityVerdict purity)
    {
        _document = document;
        _surface = surface;
        _clear = clear;
        _stepFps = stepFps > 0 ? stepFps : 30;
        Composition = composition;
        Width = width;
        Height = height;
        Purity = purity;
    }

    public string Composition { get; }
    public int Width { get; }
    public int Height { get; }
    public PurityVerdict Purity { get; }

    /// <summary>Frames rendered by the most recent <see cref="RenderAt"/> — one for a pure
    /// composition, however many were walked through for an impure one.</summary>
    public int LastCost { get; private set; }

    /// <summary>Open a composition and settle it, once.</summary>
    public static PreviewSession Open(CupriCutService cut, string composition, int width, int height, SKColor clear)
    {
        var loaded = cut.LoadComposition(composition);
        var defaults = loaded.Defaults;
        var w = width > 0 ? width : defaults?.Width > 0 ? defaults.Width : cut.Options.DefaultWidth;
        var h = height > 0 ? height : defaults?.Height > 0 ? defaults.Height : cut.Options.DefaultHeight;

        var document = cut.OpenDocument(loaded);
        try
        {
            document.Animate(0);
            if (!document.Settle(w, h, TimeSpan.FromSeconds(cut.Options.SettleTimeoutSeconds)))
                throw new TimeoutException($"'{composition}' still had resources loading after {cut.Options.SettleTimeoutSeconds}s.");

            var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException($"Could not create a {w}x{h} preview surface.");

            return new PreviewSession(document, surface, composition, w, h, clear,
                defaults?.Fps ?? cut.Options.DefaultFps, Services.Purity.Analyse(loaded));
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>The frame at <paramref name="t"/>, as an image the caller owns.</summary>
    public SKImage RenderAt(double t)
    {
        LastCost = 0;

        if (Purity.PureInTime)
        {
            Paint(t);
        }
        else if (double.IsNaN(_at) || t < _at)
        {
            // Backwards, or the first frame: state carried forward cannot be undone, so start again.
            foreach (var step in Walk(0, t)) Paint(step);
        }
        else
        {
            // Forwards: continue from where the document already is. A scrub of one frame costs one
            // frame even here, which is the whole reason the document is kept.
            foreach (var step in Walk(_at, t)) Paint(step);
        }

        _at = t;
        return _surface.Snapshot();
    }

    /// <summary>The intermediate times an impure composition must be walked through, excluding the
    /// one it is already at and including the destination.</summary>
    private IEnumerable<double> Walk(double from, double to)
    {
        var count = (int)Math.Floor((to - from) * _stepFps + 1e-9);
        for (var i = 1; i <= count; i++) yield return Math.Round(from + i / _stepFps, 6);
        yield return to;
    }

    private void Paint(double t)
    {
        _document.Animate(t);
        var canvas = _surface.Canvas;
        canvas.Clear(_clear);
        _document.Render(canvas, Width, Height);
        canvas.Flush();
        LastCost++;
    }

    public void Dispose()
    {
        _surface.Dispose();
        _document.Dispose();
    }
}
