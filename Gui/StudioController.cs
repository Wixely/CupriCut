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
public sealed partial class StudioController : IDisposable
{
    private readonly CupriCutService _cut;
    private readonly VideoEncoder _encoder;
    private readonly ILogger _log;
    private readonly StudioModel _model;
    private readonly PreviewSurface _surface = new();

    private CupriDocument? _document;
    private double _shownTime = double.NaN;
    private string? _shownProject;

    // The render thread and the handshake with it. See PreviewLoop.cs.
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _stopping = new();
    private readonly FrameClock _frameClock = new();
    private Thread? _renderThread;
    private DateTime _lastTick = DateTime.UtcNow;
    private Configuration.ServerOptions? _server;
    private bool _served;
    private int _calibrationRuns;

    public StudioController(CupriCutService cut, VideoEncoder encoder, StudioModel model, ILogger log)
    {
        _cut = cut;
        _encoder = encoder;
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
        document.OnAction("data-cut-view", e => { _model.View = e.Value; return true; });
        document.OnAction("data-cut-tab", e => { _model.SettingsTab = e.Value; return true; });

        // The region drag. Returning true on Down captures the pointer for this element, so the
        // move and up phases arrive here rather than going to the ordinary gesture recogniser.
        document.OnPointer(StudioApp.MarkAttribute, OnMark);

        Refresh();
        DescribeSettings();
        StartRenderThread();
    }

    /// <summary>The server and path settings, read once - they come from configuration that does
    /// not change under a running window.</summary>
    public void DescribeSettings()
    {
        var options = _cut.Options;
        _model.ServerUrl = _server is null ? "(not served)" : $"http://{_server.Host}:{_server.Port}{_server.Path}";
        _model.HealthUrl = _server is null ? "(not served)" : $"http://{_server.Host}:{_server.Port}/healthz";
        _model.ServerPath = _server?.Path ?? "";
        _model.ServerState = _served
            ? "MCP server running on this window"
            : "MCP server unavailable - the preview and annotations still work";
        _model.PasswordState = string.IsNullOrWhiteSpace(_server?.Password)
            ? "none (anyone who can reach the port can drive it)"
            : "set (clients must send X-MCP-Password)";

        _model.FfmpegPath = options.EnableVideo ? options.FfmpegPath : "video disabled (Cut:EnableVideo)";
        _model.OutputRootPath = _cut.OutputRoot;
        _model.ProjectRootPath = _cut.ProjectRoot;
        _model.CompositionRootPaths = string.Join("  ", _cut.CompositionRoots);
        _model.WorkersSetting = options.RenderWorkers > 0
            ? $"{options.RenderWorkers} (from Cut:RenderWorkers)"
            : $"{ParallelRenderer.DefaultWorkers} (built-in guess; calibrate to measure this machine)";
        _model.EngineVersion = CupriCutService.EngineVersion;
    }

    /// <summary>Told by the host what it is serving, so the settings page can show it.</summary>
    public void Serving(Configuration.ServerOptions server, bool served)
    {
        _server = server;
        _served = served;
        DescribeSettings();
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
            _wake.Set();
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
                _model.Playing = false;          // you cannot point at a frame that is moving
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

            case "play":
                if (_model.Selected is null) { _model.Status = "Open a project first."; return; }
                if (_model.Time >= _model.Duration - 1e-9) _model.Time = 0;
                _model.Playing = !_model.Playing;
                _lastTick = DateTime.UtcNow;
                _model.Status = _model.Playing ? "Playing." : "Paused.";
                _wake.Set();
                break;

            case "rewind":
                _model.Playing = false;
                _model.Time = 0;
                _wake.Set();
                break;

            case "calibrate":
                RunCalibration();
                break;

            case "toggle-checks":
                _model.ShowAllChecks = !_model.ShowAllChecks;
                ShowCalibration();
                break;

            case "apply-workers":
                ApplyWorkers();
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
            _model.Playing = false;
            _wake.Set();
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

    // ---- calibration -------------------------------------------------------------------------

    private CalibrationReport? _lastCalibration;

    /// <summary>Run the checks on a worker: they encode several clips and time a render, which is
    /// seconds of work and must not be done on the thread painting the window.</summary>
    private void RunCalibration()
    {
        if (_model.Calibrating) return;

        _model.Calibrating = true;
        _model.CalibrationSummary = "Encoding test clips and timing the renderer...";
        var run = Interlocked.Increment(ref _calibrationRuns);

        _ = Task.Run(() =>
        {
            try
            {
                var report = new Calibrator(_cut, _encoder, _log);
                var result = report.Run(tuneWorkers: true);

                if (Volatile.Read(ref _calibrationRuns) != run) return;   // superseded
                _lastCalibration = result;
                _model.RecommendedWorkers = report.BestWorkers ?? 0;
                _model.CalibrationSummary = result.AllPassed
                    ? $"All {result.Passed} checks passed in {result.ElapsedMs:0} ms."
                    : $"{result.Failed} of {result.Checks.Count} checks failed ({result.ElapsedMs:0} ms).";
                ShowCalibration();
            }
            catch (Exception ex)
            {
                _model.CalibrationSummary = "Calibration failed: " + ex.Message;
                _log.LogWarning(ex, "Calibration failed");
            }
            finally
            {
                if (Volatile.Read(ref _calibrationRuns) == run) _model.Calibrating = false;
            }
        });
    }

    private void ShowCalibration()
    {
        if (_lastCalibration is not { } report) return;
        var rows = _model.ShowAllChecks ? report.Checks : report.Failures;
        _model.Calibration = [.. rows.Select(c => new CalibrationRow
        {
            Group = c.Group,
            Name = c.Name,
            Detail = c.Detail,
            Fix = c.Fix ?? string.Empty,
            Ok = c.Ok,
        })];
    }

    /// <summary>Use the measured worker count for the rest of this session.
    ///
    /// <para>In memory only: writing it to CupriCut.Local.json is the CLI's job (cupricut calibrate
    /// --apply), because the window is not the place to be editing configuration behind someone's
    /// back. The status strip says so rather than leaving it to be discovered.</para></summary>
    private void ApplyWorkers()
    {
        if (_model.RecommendedWorkers <= 0) return;
        _cut.Options.RenderWorkers = _model.RecommendedWorkers;
        _model.WorkersSetting = $"{_model.RecommendedWorkers} (measured, this session only)";
        _model.CalibrationSummary =
            $"Using {_model.RecommendedWorkers} workers for this session. " +
            "Run \"cupricut calibrate --apply\" to make it permanent.";
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


    public void Dispose()
    {
        _stopping.Cancel();
        _wake.Set();
        _renderThread?.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
        _stopping.Dispose();
        _surface.Dispose();
    }
}
