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
/// <para><b>Frames are retired, never disposed on the spot.</b> The interface is explicit about
/// this: "never dispose an image the paint path may still be reading — swap first, dispose the
/// PREVIOUS frame after the next repaint (or keep a small pool)". The paint path resolves
/// <see cref="CurrentFrame"/> and then draws it, so the window between those two is one this class
/// must not free anything in. Disposing immediately survived while a preview was one frame per
/// scrub and became a crash the moment playback published sixty a second.</para>
///
/// <para>So an outgoing frame joins a short queue and is released a few publishes later, by which
/// point the painter has moved several frames on. At 1280x720 that is about 3.7 MB a frame and a
/// ceiling of some tens of megabytes — the "small pool" the contract sanctions, and bounded, so it
/// is a pool rather than a leak.</para>
/// </summary>
public sealed class PreviewSurface : ISurfaceSource, IDisposable
{
    /// <summary>How many publishes an outgoing frame survives before it is released. One would
    /// satisfy the letter of the contract; a few gives room for a painter that is mid-draw or
    /// briefly descheduled, at a cost of a few megabytes.</summary>
    private const int RetireAfter = 3;

    private readonly Lock _gate = new();
    private readonly Queue<SKImage> _retired = new();
    private SKImage? _frame;
    private bool _ticking;

    public SKImage? CurrentFrame
    {
        get { lock (_gate) return _frame; }
    }

    public (int W, int H)? NaturalSize
    {
        get { lock (_gate) return _frame is null ? null : (_frame.Width, _frame.Height); }
    }

    /// <summary>True while frames are arriving continuously — playback. That is what keeps a
    /// render-on-demand host painting instead of waiting to be told about each frame; a still
    /// preview sets it false again so an idle window costs nothing.</summary>
    public bool Ticking
    {
        get { lock (_gate) return _ticking; }
        set { lock (_gate) _ticking = value; }
    }

    /// <summary>Frames this surface is still holding: the current one plus those not yet retired.
    /// Exposed so a test can tell a bounded pool from a leak.</summary>
    public int RetainedFrames
    {
        get { lock (_gate) return _retired.Count + (_frame is null ? 0 : 1); }
    }

    /// <summary>Make <paramref name="frame"/> the frame on show. The previous one is retired rather
    /// than freed — see the note on this class.</summary>
    public void Publish(SKImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        SKImage? release = null;
        lock (_gate)
        {
            if (_frame is not null) _retired.Enqueue(_frame);
            _frame = frame;
            if (_retired.Count > RetireAfter) release = _retired.Dequeue();
        }

        // Outside the lock: disposing native memory is not something to hold a reader behind.
        release?.Dispose();
    }

    /// <summary>Drop the frame on show. Retired frames are released too, so this is for teardown
    /// and for closing a project rather than for use between frames.</summary>
    public void Clear()
    {
        SKImage?[] release;
        lock (_gate)
        {
            release = [_frame, .. _retired];
            _retired.Clear();
            _frame = null;
            _ticking = false;
        }

        foreach (var image in release) image?.Dispose();
    }

    public void Dispose() => Clear();
}
