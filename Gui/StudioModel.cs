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

    /// <summary>What just happened, shown in the status strip.</summary>
    public string Status { get; set; } = "Choose a project to preview.";

    /// <summary>True once a frame has been previewed, so the window can stop showing the placeholder.</summary>
    public bool HasFrame { get; set; }

    /// <summary>The clock is advancing in real time.</summary>
    public bool Playing { get; set; }

    /// <summary>Whether this composition needs sweeping, in the words the status strip uses.</summary>
    public string PurityNote { get; set; } = string.Empty;

    /// <summary>Annotation mode: a drag on the preview draws a region instead of doing nothing.</summary>
    public bool Marking { get; set; }

    /// <summary>The note that will be attached to the next region drawn.</summary>
    public string PendingNote { get; set; } = string.Empty;

    /// <summary>The rectangle being dragged right now, in normalised frame coordinates.</summary>
    public bool Dragging { get; set; }
    public double DragX { get; set; }
    public double DragY { get; set; }
    public double DragW { get; set; }
    public double DragH { get; set; }

    public List<AnnotationRow> Annotations { get; set; } = [];

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

/// <summary>One annotation in the list beside the preview.</summary>
public sealed class AnnotationRow
{
    public string Id { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string At { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public bool Resolved { get; set; }
    public string StatusLabel { get; set; } = "open";

    public string RowClass => Resolved ? "done" : "";
}
