using CupriFace.Paint;
using SkiaSharp;

namespace CupriCut.Gui;

/// <summary>
/// The latest previewed frame, handed to the window as pixels.
///
/// <para><c>ISurfaceSource</c> is the seam the engine already has for live pixel producers — video
/// players, 3D viewports — and a preview is exactly that. Going through it means a preview costs no
/// PNG encode and no base64 in the DOM: the sweep makes an <see cref="SKImage"/> and the window
/// paints it. Scrubbing a timeline would be unusable at 45 ms an encode.</para>
///
/// <para>Published from whichever thread rendered it and read on the UI thread, so the swap is
/// guarded. <see cref="Ticking"/> stays false: a preview is a still, and an idle window should cost
/// nothing.</para>
/// </summary>
public sealed class PreviewSurface : ISurfaceSource, IDisposable
{
    private readonly Lock _gate = new();
    private SKImage? _frame;

    public SKImage? CurrentFrame
    {
        get { lock (_gate) return _frame; }
    }

    public (int W, int H)? NaturalSize
    {
        get { lock (_gate) return _frame is null ? null : (_frame.Width, _frame.Height); }
    }

    /// <summary>False: a preview is a still frame, not playback. The window repaints when a new one
    /// arrives rather than running a loop.</summary>
    public bool Ticking => false;

    /// <summary>Replace the frame on show. The previous one is disposed here, so the caller can
    /// hand over a snapshot and forget it.</summary>
    public void Publish(SKImage frame)
    {
        SKImage? old;
        lock (_gate)
        {
            old = _frame;
            _frame = frame;
        }
        old?.Dispose();
    }

    public void Clear()
    {
        SKImage? old;
        lock (_gate)
        {
            old = _frame;
            _frame = null;
        }
        old?.Dispose();
    }

    public void Dispose() => Clear();
}
