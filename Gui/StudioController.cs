using System.Globalization;
using CupriCut.Services;
using CupriFace;
using CupriFace.Interaction;
using CupriFace.Dom;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CupriCut.Gui;

/// <summary>
/// What the studio window does: load projects, render previews off the UI thread, and turn a drag
/// on the frame into an annotation the agent can read.
///
/// <para>Separate from <see cref="StudioApp"/>, which is only what the window <i>is</i>. Everything
/// here is testable without a window — the preview path ends at an <see cref="SKImage"/>, and the
/// drag maths is a pure function of a rectangle.</para>
/// </summary>
public sealed class StudioController : IDisposable
{
    private readonly CupriCutService _cut;
    private readonly ILogger _log;
    private readonly StudioModel _model;
    private readonly PreviewSurface _surface = new();

    private CupriDocument? _document;
    private CancellationTokenSource? _pending;
    private double _shownTime = double.NaN;
    private string? _shownProject;

    public StudioController(CupriCutService cut, StudioModel model, ILogger log)
    {
        _cut = cut;
        _model = model;
        _log = log;
    }

    public StudioModel Model => _model;

    public PreviewSurface Surface => _surface;

    // ---- wiring ---------------------------------------------------------------------------

    /// <summary>Attach to the window's document: publish previews into it, and take its input.</summary>
    public void Attach(CupriDocument document)
    {
        _document = document;
        document.Surfaces.Register(StudioApp.PreviewKey, _surface);

        document.OnAction("data-cut-open", e => { Open(e.Value); return true; });
        document.OnAction("data-cut-action", e => { Command(e.Value); return true; });
        document.OnAction("data-cut-goto", e => { GoTo(e.Value); return true; });
        document.OnAction("data-cut-delete", e => { Delete(e.Value); return true; });

        // The region drag. Returning true on Down captures the pointer for this element, so the
        // move and up phases arrive here rather than going to the ordinary gesture recogniser.
        document.OnPointer(StudioApp.MarkAttribute, OnMark);

        Refresh();
    }

    /// <summary>Reload the project list from disk. Cheap, and the only thing that notices a project
    /// an agent saved while the window was open.</summary>
    public void Refresh()
    {
        try
        {
            _model.Projects = [.. _cut.ListProjects().Select(file =>
            {
                try
                {
                    var project = _cut.LoadProject(file);
                    var open = project.OpenAnnotations.Count();
                    return new ProjectRow
                    {
                        File = file,
                        Name = string.IsNullOrWhiteSpace(project.Name) ? file : project.Name,
                        Detail = $"{project.Render.Width}x{project.Render.Height} · {project.Render.Fps:0.##}fps · {project.Render.Duration:0.##}s",
                        IsSelected = string.Equals(file, _model.Selected, StringComparison.OrdinalIgnoreCase),
                        Badge = open == 0 ? string.Empty : $"{open} open annotation{(open == 1 ? "" : "s")}",
                    };
                }
                catch (Exception ex)
                {
                    return new ProjectRow { File = file, Name = file, Detail = "unreadable: " + ex.Message };
                }
            })];
        }
        catch (Exception ex)
        {
            _model.Status = "Could not list projects: " + ex.Message;
        }
    }

    // ---- commands -------------------------------------------------------------------------

    private void Open(string file)
    {
        try
        {
            var project = _cut.LoadProject(file);
            _model.Selected = file;
            _model.SelectedTitle = string.IsNullOrWhiteSpace(project.Name) ? file : project.Name;
            _model.Duration = project.Render.Duration > 0 ? project.Render.Duration : 3;
            _model.FrameWidth = project.Render.Width;
            _model.FrameHeight = project.Render.Height;
            _model.Time = 0;
            _model.Status = $"Opened {file}.";
            ReloadAnnotations(project);
            Refresh();
            RequestPreview(force: true);
        }
        catch (Exception ex)
        {
            _model.Status = "Could not open: " + ex.Message;
        }
    }

    private void Command(string command)
    {
        switch (command)
        {
            case "mark":
                if (_model.Selected is null) { _model.Status = "Open a project first."; return; }
                _model.Marking = !_model.Marking;
                _model.Status = _model.Marking
                    ? "Drag a box on the frame round the thing that is wrong."
                    : "Marking off.";
                break;

            case "cancel":
                _model.Marking = false;
                _model.Dragging = false;
                _model.Status = "Marking cancelled.";
                break;
        }
    }

    private void GoTo(string id)
    {
        if (_model.Selected is null) return;
        try
        {
            var annotation = _cut.LoadProject(_model.Selected).Annotations
                .FirstOrDefault(a => a.Id == id);
            if (annotation is null) return;
            _model.Time = annotation.Time;
            _model.Status = $"Jumped to {annotation.Time.ToString("0.###", CultureInfo.InvariantCulture)}s — \"{annotation.Note}\".";
            RequestPreview(force: true);
        }
        catch (Exception ex)
        {
            _model.Status = "Could not jump: " + ex.Message;
        }
    }

    private void Delete(string id)
    {
        if (_model.Selected is null) return;
        try
        {
            var project = _cut.EditProject(_model.Selected, p => p.Annotations.RemoveAll(a => a.Id == id));
            ReloadAnnotations(project);
            Refresh();
            _model.Status = $"Deleted annotation {id}.";
        }
        catch (Exception ex)
        {
            _model.Status = "Could not delete: " + ex.Message;
        }
    }

    // ---- the region drag --------------------------------------------------------------------

    private bool OnMark(MultiPointerEvent e)
    {
        if (!_model.Marking || _model.Selected is null) return false;   // decline; ordinary input proceeds

        var box = FindPreviewBox();
        if (box is not { } rect) return false;

        var (x, y) = Normalise(rect, e.X, e.Y);

        switch (e.Phase)
        {
            case PointerPhase.Down:
                _model.Dragging = true;
                _model.DragX = x;
                _model.DragY = y;
                _model.DragW = 0;
                _model.DragH = 0;
                return true;

            case PointerPhase.Move when _model.Dragging:
                _model.DragW = x - _model.DragX;
                _model.DragH = y - _model.DragY;
                return true;

            case PointerPhase.Up when _model.Dragging:
                _model.Dragging = false;
                _model.DragW = x - _model.DragX;
                _model.DragH = y - _model.DragY;
                CommitRegion();
                return true;
        }

        return false;
    }

    /// <summary>Turn the dragged rectangle into an annotation on the project.</summary>
    private void CommitRegion()
    {
        if (_model.Selected is null) return;

        // A tap is not a region. Below about half a percent of the frame in either direction it is
        // almost certainly a misclick, and a zero-area annotation points at nothing.
        if (Math.Abs(_model.DragW) < 0.005 || Math.Abs(_model.DragH) < 0.005)
        {
            _model.Status = "That was a tap, not a region — drag a box.";
            return;
        }

        var note = string.IsNullOrWhiteSpace(_model.PendingNote)
            ? "(no note — the reviewer marked this region)"
            : _model.PendingNote.Trim();

        var annotation = new Annotation
        {
            Time = _model.Time,
            X = _model.DragX,
            Y = _model.DragY,
            W = _model.DragW,
            H = _model.DragH,
            Note = note,
            Author = "reviewer",
        }.Normalised();

        try
        {
            var project = _cut.EditProject(_model.Selected, p => p.Annotations.Add(annotation));
            ReloadAnnotations(project);
            Refresh();
            _model.PendingNote = string.Empty;
            _model.Marking = false;
            _model.Status = $"Marked {annotation.Describe(_model.FrameWidth, _model.FrameHeight)}";
            _log.LogInformation("Annotation {Id} added to {Project}", annotation.Id, _model.Selected);
        }
        catch (Exception ex)
        {
            _model.Status = "Could not save the annotation: " + ex.Message;
        }
    }

    /// <summary>
    /// The preview element's box in the same coordinates a pointer arrives in.
    ///
    /// <para><b>Absolute, not local.</b> <c>RenderNode.X</c> and <c>Y</c> are relative to the
    /// PARENT, and the preview sits four levels deep, so using them raw put the box somewhere the
    /// pointer never is — and every annotation would have been normalised against the wrong
    /// rectangle and stored pointing at the wrong part of the frame. It rendered fine and it wrote
    /// a plausible-looking region, which is exactly the kind of wrong that survives review.</para>
    ///
    /// <para>The origin is accumulated the way <c>HitTesting.Hit</c> accumulates it — including the
    /// scroll offsets, since a scrolled ancestor shifts its children for the pointer too.</para>
    /// </summary>
    private SKRect? FindPreviewBox()
    {
        if (_document is null) return null;
        return Locate(_document.Root, 0, 0, StudioApp.PreviewKey);
    }

    private static SKRect? Locate(RenderNode node, float originX, float originY, string key)
    {
        var ax = originX + node.X;
        var ay = originY + node.Y;
        if (node.SurfaceKey == key) return SKRect.Create(ax, ay, node.Width, node.Height);

        var childX = ax - (node.IsScrollableX ? node.EffectiveScrollX : 0f);
        var childY = ay - (node.IsScrollable ? node.EffectiveScrollY : 0f);
        foreach (var child in node.Children)
            if (Locate(child, childX, childY, key) is { } hit) return hit;
        return null;
    }

    /// <summary>Window coordinates to 0–1 of the frame. Pure arithmetic, so it is testable without
    /// a window — which is the only reason the drag maths can be trusted here at all.</summary>
    public static (double X, double Y) Normalise(SKRect box, float x, float y)
    {
        if (box.Width <= 0 || box.Height <= 0) return (0, 0);
        return (Math.Clamp((x - box.Left) / box.Width, 0, 1),
                Math.Clamp((y - box.Top) / box.Height, 0, 1));
    }

    private void ReloadAnnotations(CutProject project)
    {
        _model.Annotations = [.. project.Annotations
            .OrderBy(a => a.Time)
            .Select(a =>
            {
                var (px, py, pw, ph) = a.InPixels(project.Render.Width, project.Render.Height);
                return new AnnotationRow
                {
                    Id = a.Id,
                    Note = a.Note,
                    At = "t = " + a.Time.ToString("0.###", CultureInfo.InvariantCulture) + "s",
                    Region = $"{pw}x{ph} at ({px},{py})",
                    Resolved = a.Status == AnnotationStatus.Resolved,
                    StatusLabel = a.Status == AnnotationStatus.Resolved ? "resolved" : "open",
                };
            })];
    }

    // ---- preview --------------------------------------------------------------------------

    /// <summary>Call each frame from the window loop. Notices the scrub bar moving and starts a
    /// render; the model is bound two-way, so there is no change event to hook.</summary>
    public void Tick()
    {
        if (_model.Selected is null) return;
        if (Math.Abs(_model.Time - _shownTime) < 1e-6 && _model.Selected == _shownProject) return;
        RequestPreview(force: false);
    }

    /// <summary>Render the current project at the current time, off the UI thread.</summary>
    private void RequestPreview(bool force)
    {
        if (_model.Selected is not { } project) return;

        var time = _model.Time;
        if (!force && Math.Abs(time - _shownTime) < 1e-6 && project == _shownProject) return;

        _shownTime = time;
        _shownProject = project;

        // One preview at a time. A scrub drags through dozens of values a second and every one of
        // them would otherwise queue a sweep from zero; cancelling the previous is what keeps the
        // window responsive rather than minutes behind the cursor.
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        _model.Rendering = true;
        _ = Task.Run(() => RenderPreview(project, time, cts.Token), cts.Token);
    }

    private void RenderPreview(string project, double time, CancellationToken token)
    {
        try
        {
            SKImage? captured = null;
            var report = _cut.Sweep(
                new SweepSpec { Composition = project, Times = [time] },
                frame =>
                {
                    // The sweep owns its image only until the sink returns, so take a copy that can
                    // outlive it and live on the window's surface.
                    using var bitmap = FrameEncoder.Copy(frame.Image);
                    captured = SKImage.FromBitmap(bitmap);
                });

            if (token.IsCancellationRequested)
            {
                captured?.Dispose();
                return;
            }

            if (captured is not null)
            {
                _surface.Publish(captured);
                _model.HasFrame = true;

                // Re-registering is how a new frame is announced: the registry flags an arrival and
                // the host repaints. (The app's refresh interval is the belt to this brace.)
                _document?.Surfaces.Register(StudioApp.PreviewKey, _surface);
            }

            _model.Status =
                $"t = {time.ToString("0.###", CultureInfo.InvariantCulture)}s · swept {report.StepsRendered} frames in {report.ElapsedMs:0}ms";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scrub position; nothing to say.
        }
        catch (Exception ex)
        {
            _model.Status = "Preview failed: " + ex.Message;
            _log.LogWarning(ex, "Preview of {Project} at {Time}s failed", project, time);
        }
        finally
        {
            if (ReferenceEquals(_pending, null) || _pending!.Token == token || !token.CanBeCanceled)
                _model.Rendering = false;
        }
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _surface.Dispose();
    }
}
