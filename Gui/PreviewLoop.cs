using System.Globalization;
using CupriCut.Services;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CupriCut.Gui;

/// <summary>
/// The preview half of <see cref="StudioController"/>: one thread that owns the preview document
/// for its whole life and renders whatever time the model currently names.
///
/// <para><b>Why a thread and not a task per frame.</b> A <c>CupriDocument</c> is single-threaded, so
/// the document has to live somewhere fixed if it is to be reused at all — and reusing it is the
/// point. A scrub bar produces dozens of positions a second; the shape that works is a worker with
/// a latest-wins request, where superseded positions are simply never rendered. The previous
/// design started a task per position, each loading and settling the composition afresh: about
/// 160 ms of which roughly 5 ms was the frame.</para>
/// </summary>
public sealed partial class StudioController
{
    /// <summary>Nudge the render thread. Called from the window loop, which is the only thread that
    /// writes <c>Time</c>.</summary>
    public void Tick()
    {
        if (_model.Playing
            || !string.Equals(_model.Selected, _shownProject, StringComparison.Ordinal)
            || Math.Abs(_model.Time - _shownTime) > 1e-9
            || _model.OverlayVersion != _shownOverlay)
        {
            _shownProject = _model.Selected;
            _wake.Set();
        }
    }

    /// <summary>Everything the overlay needs, taken in one go on the render thread.
    ///
    /// <para>Snapshotted rather than read field by field while drawing: the UI thread is moving the
    /// drag rectangle as this runs, and a marquee assembled from half-old and half-new numbers
    /// would shear.</para>
    /// </summary>
    private OverlayState CaptureOverlay()
    {
        var editing = _model.EditingId;
        var marks = _model.Marks;
        return new OverlayState(
            _model.Time,
            [.. marks.Select(m => new OverlayMark(m, m.Id == editing))],
            _model.Dragging,
            _model.DragX, _model.DragY, _model.DragW, _model.DragH);
    }

    private void StartRenderThread()
    {
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "cupricut-preview",
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        PreviewSession? session = null;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                // A timeout rather than a pure wait: playback has to advance without anything
                // nudging it, and it also picks up a project an agent saved while the window is open.
                _wake.Wait(TimeSpan.FromMilliseconds(_model.Playing ? 4 : 200));
                _wake.Reset();
                if (_stopping.IsCancellationRequested) break;

                // Ticking is how a render-on-demand host is told to keep painting rather than
                // waiting to be nudged per frame. True exactly while the clock is running.
                _surface.Ticking = _model.Playing;

                if (_model.Playing) Advance();

                var wanted = _model.Selected;
                if (wanted is null)
                {
                    session?.Dispose();
                    session = null;
                    _surface.Clear();
                    _model.HasFrame = false;
                    continue;
                }

                if (session is null || !string.Equals(session.Composition, wanted, StringComparison.Ordinal))
                {
                    session?.Dispose();
                    session = null;
                    session = TryOpen(wanted);
                    if (session is null) continue;
                    _shownTime = double.NaN;
                }

                var time = _model.Time;
                var overlay = _model.OverlayVersion;
                var moved = Math.Abs(time - _shownTime) > 1e-9;
                var redrawn = overlay != _shownOverlay;
                if (!moved && !redrawn) continue;

                try
                {
                    var state = CaptureOverlay();
                    void Paint(SKCanvas canvas, int w, int h) => AnnotationOverlay.Draw(canvas, w, h, state);

                    // A drag moves the overlay without moving the clock, so the same time is
                    // repainted rather than skipped.
                    var frame = moved ? session.RenderAt(time, Paint) : session.Repaint(Paint);
                    if (frame is null) continue;
                    _surface.Publish(frame);
                    _shownOverlay = overlay;
                    _model.HasFrame = true;
                    _shownTime = time;

                    // Re-registering is how a new frame is announced: the registry flags an arrival
                    // and the host repaints.
                    _document?.Surfaces.Register(StudioApp.PreviewKey, _surface);
                    ReportFrame(session, time);
                }
                catch (Exception ex)
                {
                    _model.Playing = false;
                    _model.Status = "Preview failed: " + ex.Message;
                    _log.LogWarning(ex, "Preview of {Composition} at {Time}s failed", wanted, time);
                    _shownTime = time;   // do not spin on the same failing frame
                    _shownOverlay = overlay;
                }
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private PreviewSession? TryOpen(string composition)
    {
        _model.Rendering = true;
        try
        {
            var session = PreviewSession.Open(_cut, composition, _model.FrameWidth, _model.FrameHeight, PreviewBackground(composition));
            _model.PurityNote = session.Purity.Summary;
            return session;
        }
        catch (Exception ex)
        {
            _model.Status = "Could not preview: " + ex.Message;
            _model.Playing = false;
            _model.Selected = null;
            _log.LogWarning(ex, "Could not open {Composition} for preview", composition);
            return null;
        }
        finally
        {
            _model.Rendering = false;
        }
    }

    /// <summary>Move the clock by real elapsed time. It stops at the end rather than looping: a
    /// preview that wraps silently is one you cannot tell has finished.</summary>
    private void Advance()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        // A first tick, or a thread that was descheduled, would otherwise jump the clock.
        if (elapsed <= 0 || elapsed > 0.5) return;

        var next = _model.Time + elapsed;
        if (next >= _model.Duration)
        {
            _model.Time = _model.Duration;
            _model.Playing = false;
            _model.Status = $"Reached the end at {_model.Duration.ToString("0.###", CultureInfo.InvariantCulture)}s.";
            return;
        }
        _model.Time = next;
    }

    private void ReportFrame(PreviewSession session, double time)
    {
        var fps = _frameClock.Tick();
        var at = time.ToString("0.###", CultureInfo.InvariantCulture);
        _model.RenderStat = session.Purity.PureInTime
            ? $"t = {at}s · direct render · {fps:0} fps"
            : $"t = {at}s · walked {session.LastCost} frame(s) · {fps:0} fps";
    }

    /// <summary>The project's own clear colour, so the preview matches what a render would produce.</summary>
    private SKColor PreviewBackground(string composition)
    {
        try
        {
            var project = _cut.LoadProject(composition);
            if (project.Render.Alpha) return SKColors.Transparent;
            if (!string.IsNullOrWhiteSpace(project.Render.Background)
                && SKColor.TryParse(project.Render.Background, out var colour)) return colour;
        }
        catch
        {
            // A plain composition rather than a project, or an unreadable one. The clear colour is
            // not worth failing a preview over.
        }
        return SKColors.White;
    }

    /// <summary>A rolling frames-per-second, so the status strip can say whether the preview is
    /// keeping up rather than leaving it to be guessed.</summary>
    private sealed class FrameClock
    {
        private readonly Queue<DateTime> _stamps = new();

        public double Tick()
        {
            var now = DateTime.UtcNow;
            _stamps.Enqueue(now);
            while (_stamps.Count > 0 && (now - _stamps.Peek()).TotalSeconds > 1) _stamps.Dequeue();
            return _stamps.Count;
        }
    }
}
