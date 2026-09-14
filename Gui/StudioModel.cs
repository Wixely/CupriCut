using System.Globalization;
using CupriCut.Services;

namespace CupriCut.Gui;

/// <summary>
/// What the studio window is showing, as plain properties the engine binds to.
///
/// <para>No <c>INotifyPropertyChanged</c>: CupriFace binds to ordinary objects and re-reads them
/// each rebuild, so this stays a bag of values. It is mutated from the UI thread and from the
/// render worker, so the few fields the worker touches are swapped whole rather than edited.</para>
/// </summary>
public sealed class StudioModel
{
    /// <summary>Projects on offer, newest first.</summary>
    public List<ProjectRow> Projects { get; set; } = [];

    /// <summary>The project being previewed, or null before one is chosen.</summary>
    public string? Selected { get; set; }

    public string SelectedTitle { get; set; } = "No project selected";

    /// <summary>Where the scrub bar is, in seconds.</summary>
    public double Time { get; set; }

    /// <summary>The project's declared length, which bounds the scrub bar.</summary>
    public double Duration { get; set; } = 3;

    public int FrameWidth { get; set; } = 1280;
    public int FrameHeight { get; set; } = 720;

    /// <summary>A render is in flight. The preview keeps showing the last good frame meanwhile.</summary>
    public bool Rendering { get; set; }

    /// <summary>What the reviewer just did, or what went wrong. Written only in response to an
    /// action, so it stays on screen long enough to be read.
    ///
    /// <para>Separate from <see cref="RenderStat"/> on purpose: the render loop reports a frame
    /// many times a second, and when both shared one line every message worth reading - "that was
    /// a tap, not a region" - was wiped before it could be.</para></summary>
    public string Status { get; set; } = "Choose a project to preview.";

    /// <summary>The last frame's cost: time, how it was reached, and the rate. Overwritten
    /// constantly, which is why it is not the status line.</summary>
    public string RenderStat { get; set; } = string.Empty;

    /// <summary>True once a frame has been previewed, so the window can stop showing the placeholder.</summary>
    public bool HasFrame { get; set; }

    // ---- which page, and which tab of it ----------------------------------------------------
    //
    // The engine binds {{Path}} and data-repeat and has no conditional attribute, so "show this
    // view" is a computed class driving display:none - the same mechanism the empty states use.
    // Fine for a handful of views; it would want rethinking past a dozen.

    /// <summary>"studio", "projects" or "settings".</summary>
    public string View { get; set; } = "studio";

    /// <summary>Which settings tab: "server", "render" or "calibrate".</summary>
    public string SettingsTab { get; set; } = "server";

    public string StudioClass => View == "studio" ? "" : "hidden";
    public string ProjectsClass => View == "projects" ? "" : "hidden";
    public string SettingsClass => View == "settings" ? "" : "hidden";
    public string StudioNavClass => View == "studio" ? "navon" : "";
    public string ProjectsNavClass => View == "projects" ? "navon" : "";
    public string SettingsNavClass => View == "settings" ? "navon" : "";

    /// <summary>The projects grouped by the folder they are in, for the board. Rebuilt whenever
    /// the project list is, because a folder here IS a directory on disk - there is no second
    /// record of it that could disagree.</summary>
    public List<FolderColumn> Folders { get; set; } = [];

    /// <summary>What the "new folder" field holds.</summary>
    public string NewFolder { get; set; } = string.Empty;

    public string NoFoldersClass => Folders.Count == 0 ? "" : "hidden";

    public string ServerTabClass => SettingsTab == "server" ? "" : "hidden";
    public string RenderTabClass => SettingsTab == "render" ? "" : "hidden";
    public string CalibrateTabClass => SettingsTab == "calibrate" ? "" : "hidden";
    public string ServerTabNav => SettingsTab == "server" ? "tabon" : "";
    public string RenderTabNav => SettingsTab == "render" ? "tabon" : "";
    public string CalibrateTabNav => SettingsTab == "calibrate" ? "tabon" : "";

    // ---- the server tab ---------------------------------------------------------------------

    /// <summary>The MCP endpoint, as a client would be given it.</summary>
    public string ServerUrl { get; set; } = "";

    public string ServerState { get; set; } = "";
    public string ServerPath { get; set; } = "";
    public string HealthUrl { get; set; } = "";
    public string PasswordState { get; set; } = "";

    // ---- the render tab ---------------------------------------------------------------------

    public string FfmpegPath { get; set; } = "";
    public string OutputRootPath { get; set; } = "";
    public string ProjectRootPath { get; set; } = "";
    public string CompositionRootPaths { get; set; } = "";
    public string WorkersSetting { get; set; } = "";
    public string EngineVersion { get; set; } = "";

    // ---- the calibrate tab ------------------------------------------------------------------

    public List<CalibrationRow> Calibration { get; set; } = [];

    /// <summary>A calibration run is in flight - it takes a few seconds.</summary>
    public bool Calibrating { get; set; }

    public string CalibrationSummary { get; set; } = "Not run yet.";

    /// <summary>Show every check rather than only the failures.</summary>
    public bool ShowAllChecks { get; set; }

    public string CalibratingClass => Calibrating ? "" : "hidden";
    public string ShowAllLabel => ShowAllChecks ? "Showing all checks" : "Showing failures only";
    public string NoCalibrationClass => Calibration.Count == 0 ? "" : "hidden";
    public string ApplyWorkersClass => RecommendedWorkers > 0 ? "" : "hidden";
    public int RecommendedWorkers { get; set; }
    public string ApplyWorkersLabel => $"Use {RecommendedWorkers} workers";

    /// <summary>The clock is advancing in real time.</summary>
    public bool Playing { get; set; }

    /// <summary>Whether this composition needs sweeping, in the words the status strip uses.</summary>
    public string PurityNote { get; set; } = string.Empty;

    /// <summary>Annotation mode: a drag on the preview draws a region instead of doing nothing.</summary>
    public bool Marking { get; set; }

    /// <summary>The note that will be attached to the next region drawn.</summary>
    public string PendingNote { get; set; } = string.Empty;

    /// <summary>Whether the open composition's marked backdrop is drawn.
    ///
    /// <para>Written by the checkbox, which binds two-way - there is no event to hook, so the
    /// render loop notices it changed by comparing it against what the open session was built
    /// with. Turning it off is a change to the MARKUP, so the document is reopened.</para></summary>
    public bool ShowBackground { get; set; } = true;

    /// <summary>Whether the open composition marks a backdrop at all. A switch that would do
    /// nothing is worse than no switch, so the row is hidden when it would.</summary>
    public bool HasBackdrop { get; set; }

    /// <summary>Empty when there is a backdrop to toggle, "hidden" when there is not.</summary>
    public string BackdropClass => HasBackdrop ? "" : "hidden";

    /// <summary>What the export dropdown is set to. One of <c>ExportFormats.Names</c>, or one of
    /// the bundles below.</summary>
    public string ExportFormat { get; set; } = "mp4";

    /// <summary>Whether the format dropdown's list is showing.
    ///
    /// <para>Required, not optional. A <c>cupri-select</c> keeps its open state in the MODEL, and
    /// one without an <c>open</c> binding is a trigger that can never open - it still swallows the
    /// click and reports it handled, so it looks like a dropdown and behaves like a label. The
    /// engine has a diagnostic for exactly this (CF0021), which is why the markup is checked by
    /// CupriDoctor in the tests.</para></summary>
    public bool FormatOpen { get; set; }

    /// <summary>True while an export is running on a worker. The button says so and refuses to
    /// start a second one, because they would share the render gate and queue anyway.</summary>
    public bool Exporting { get; set; }

    public string ExportLabel => Exporting ? "Exporting\u2026" : "Export";

    /// <summary>Where the last export landed. Empty until there has been one, which is what hides
    /// the button - an "open folder" that opens nothing is worse than no button.</summary>
    public string LastExportFolder { get; set; } = string.Empty;

    public string OpenFolderClass => LastExportFolder.Length > 0 ? "" : "hidden";

    public string ExportClass => Exporting ? "armed" : "";

    /// <summary>The rectangle being dragged right now, in normalised frame coordinates.</summary>
    public bool Dragging { get; set; }
    public double DragX { get; set; }
    public double DragY { get; set; }
    public double DragW { get; set; }
    public double DragH { get; set; }

    public List<AnnotationRow> Annotations { get; set; } = [];

    /// <summary>The annotations themselves, for the overlay. Kept beside the display rows so the
    /// render thread has the geometry without re-reading the project on every frame.</summary>
    public List<Annotation> Marks { get; set; } = [];

    /// <summary>The annotation whose note is being rewritten, or empty.</summary>
    public string EditingId { get; set; } = string.Empty;

    /// <summary>The note as it is being typed. Bound two-way to the edit field.</summary>
    public string EditingNote { get; set; } = string.Empty;

    public string EditingClass => string.IsNullOrEmpty(EditingId) ? "hidden" : "";

    /// <summary>Bumped whenever the overlay would look different without the clock moving - a drag
    /// in progress, an annotation added or edited. The render loop watches it so the marquee
    /// follows the pointer.</summary>
    public int OverlayVersion { get; set; }

    // ---- what the markup can actually ask for --------------------------------------------
    //
    // The engine binds {{Path}} and data-repeat, and that is the whole vocabulary: there is no
    // conditional attribute and no expression syntax. So "show this only when..." is a computed
    // CLASS on the model, toggling `display:none` through ordinary CSS. Putting the condition here
    // rather than in the markup is also the reason it can be unit-tested.

    private const string Hidden = "hidden";

    public string TimeLabel => Time.ToString("0.00", CultureInfo.InvariantCulture) + "s";

    public string DurationLabel => Duration.ToString("0.##", CultureInfo.InvariantCulture) + "s";

    public string SizeLabel => $"{FrameWidth}x{FrameHeight}";

    public string OpenCount => Annotations.Count(a => !a.Resolved).ToString(CultureInfo.InvariantCulture);

    /// <summary>Empty-state copy shows only when the list really is empty.</summary>
    public string NoProjectsClass => Projects.Count == 0 ? "" : Hidden;

    public string NoAnnotationsClass => Annotations.Count == 0 ? "" : Hidden;

    /// <summary>The placeholder sits over the preview until the first frame arrives.</summary>
    public string PlaceholderClass => HasFrame ? Hidden : "";

    public string SpinnerClass => Rendering ? "" : Hidden;

    public string CancelClass => Marking ? "" : Hidden;

    public string MarkClass => Marking ? "armed" : "";

    public string MarkLabel => Marking ? "Marking: drag on the frame" : "Mark a region";

    public string PlayLabel => Playing ? "Pause" : "Play";

    public string PlayClass => Playing ? "armed" : "";

    /// <summary>The scrub bar is a 0–100 slider, because a slider bound to seconds would need its
    /// range to change with the project and re-binding a live control mid-drag is not worth it.</summary>
    public double Scrub
    {
        get => Duration <= 0 ? 0 : Math.Clamp(Time / Duration * 100, 0, 100);
        set => Time = Math.Clamp(value / 100 * Duration, 0, Duration);
    }
}


/// <summary>One folder's column on the projects board.</summary>
public sealed class FolderColumn
{
    /// <summary>The folder's relative path, or empty for the top level. This is what a move is
    /// addressed to, and what the column carries as an attribute so a drop can be resolved back to
    /// a destination.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>What the column is headed. The top level has no name of its own, and "" is not a
    /// heading.</summary>
    public string Title => Path.Length == 0 ? "Top level" : Path;

    public List<ProjectCard> Projects { get; set; } = [];

    public string Count => Projects.Count == 1 ? "1 project" : $"{Projects.Count} projects";

    /// <summary>Shown in an empty column, because a column with nothing in it and nothing said is
    /// indistinguishable from one that failed to load.</summary>
    public string EmptyClass => Projects.Count == 0 ? "" : "hidden";
}

/// <summary>One project card on the board.</summary>
public sealed class ProjectCard
{
    public string File { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

/// <summary>One project in the picker.</summary>
public sealed class ProjectRow
{
    public string File { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public bool IsSelected { get; set; }

    /// <summary>"on" when this is the open project. A class, because the markup has no conditional.</summary>
    public string RowClass => IsSelected ? "on" : "";

    public string BadgeClass => string.IsNullOrEmpty(Badge) ? "hidden" : "";

    /// <summary>Open annotations, as a badge. Empty string when there are none, so the markup can
    /// hide the badge by testing for content.</summary>
    public string Badge { get; set; } = string.Empty;
}

/// <summary>One row of the calibration table.</summary>
public sealed class CalibrationRow
{
    public string Group { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Fix { get; set; } = string.Empty;
    public bool Ok { get; set; }

    public string Mark => Ok ? "PASS" : "FAIL";
    public string RowClass => Ok ? "pass" : "fail";
    public string FixClass => string.IsNullOrEmpty(Fix) ? "hidden" : "";
}

/// <summary>One annotation in the list beside the preview.</summary>
public sealed class AnnotationRow
{
    public string Id { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string At { get; set; } = string.Empty;

    /// <summary>"frame 144 at 120 fps", or empty when the note predates frame stamping.</summary>
    public string FrameAt { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public bool Resolved { get; set; }
    public string StatusLabel { get; set; } = "open";

    public string RowClass => Resolved ? "done" : "";

    /// <summary>Set while this row's note is being rewritten, so the list can show which.</summary>
    public bool Editing { get; set; }

    public string EditClass => Editing ? "editing" : "";
}
