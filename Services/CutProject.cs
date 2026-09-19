using System.Text.Json;
using System.Text.Json.Serialization;

namespace CupriCut.Services;

/// <summary>
/// A composition and everything needed to re-render it, in one file.
///
/// <para><b>Why this exists.</b> Everything else here renders a file the agent had to put somewhere
/// itself. A project gives the work a home CupriCut owns, so a second MCP run - a different
/// session, no shared context, possibly no filesystem tool at all - can open what the first one
/// made, read the HTML and CSS, change one rule, and re-render at the same size and rate without
/// being told any of it.</para>
///
/// <para><b>Self-contained on purpose.</b> Assets are inlined as <c>data:</c> URIs rather than
/// referenced by path. The engine's <c>SourceResolver</c> takes a <c>data:</c> URI anywhere it
/// takes a path, so this costs nothing at render time and means a project can be copied, committed
/// or handed to another machine as the single file it appears to be.</para>
/// </summary>
public sealed class CutProject
{
    /// <summary>Format version. Bumped only when an older reader would get it wrong.</summary>
    [JsonPropertyName("cupricut")]
    public int FormatVersion { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The composition's markup, exactly as an <c>.html</c> composition would hold it.</summary>
    public string Html { get; set; } = string.Empty;

    /// <summary>The composition's stylesheet. Kept apart from the HTML so an agent can rewrite the
    /// motion without resending the markup.</summary>
    public string? Css { get; set; }


/// <summary>The settings that regenerate the animation. Every one of them is a default for the
    /// matching tool argument, never an override of one the caller gave.</summary>
    public RenderSettings Render { get; set; } = new();

    /// <summary>Inlined assets, keyed by the name the HTML and CSS refer to them by. Values are
    /// <c>data:</c> URIs.</summary>
    public Dictionary<string, string> Assets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The track this animation is timed to, and the cues read out of it - or null, which
    /// is what nearly every project is. See <see cref="ProjectAudio"/>.</summary>
    public ProjectAudio? Audio { get; set; }

    /// <summary>Regions of the frame a reviewer marked, with what they said about each. Kept here
    /// rather than in a sidecar because this file is already "everything needed to regenerate and
    /// judge this animation", and review feedback is part of judging it.</summary>
    public List<Annotation> Annotations { get; set; } = [];

    /// <summary>Free-text notes: what this is for, what was tried, what to fix next. The field a
    /// second run reads to find out what the first one was thinking.</summary>
    public ProjectMeta Meta { get; set; } = new();

    /// <summary>Annotations still waiting on someone.</summary>
    public IEnumerable<Annotation> OpenAnnotations =>
        Annotations.Where(a => a.Status == AnnotationStatus.Open);

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The extension that makes a path a project. Checked rather than sniffed, so a plain
    /// HTML composition never accidentally parses as one.</summary>
    public const string Extension = ".cut.json";

    /// <summary>Either shape counts. <c>.cut.json</c> is the plain one, readable and diffable and
    /// right for everything without a payload in it; <c>.cutpkg</c> is the container, for a project
    /// carrying audio or images - see <see cref="CutPackage"/>.</summary>
    public static bool IsProjectPath(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(CutPackage.Extension, StringComparison.OrdinalIgnoreCase);

    public static CutProject Parse(string json, string path)
    {
        CutProject? project;
        try
        {
            project = JsonSerializer.Deserialize<CutProject>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"'{path}' is not valid CupriCut project JSON: {ex.Message}", ex);
        }

        if (project is null) throw new InvalidOperationException($"'{path}' parsed as an empty project.");
        if (project.FormatVersion > 1)
            throw new InvalidOperationException(
                $"'{path}' is format version {project.FormatVersion}; this CupriCut understands version 1. Upgrade CupriCut rather than editing the file.");
        return project;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>A copy whose <see cref="Assets"/> may be rewritten to point at package entries.
    ///
    /// <para>Shallow everywhere except the asset dictionary, which is the only thing packaging
    /// touches - and a copy of it specifically so the caller's project does not come back with its
    /// payloads replaced by paths. It is very likely about to be rendered.</para></summary>
    public CutProject CloneForPackaging() => new()
    {
        FormatVersion = FormatVersion,
        Name = Name,
        Description = Description,
        Html = Html,
        Css = Css,
        Render = Render,
        Audio = Audio,
        Meta = Meta,
        Annotations = Annotations,
        Assets = new Dictionary<string, string>(Assets, StringComparer.Ordinal),
    };
}

    /// <summary>
/// A track a composition is timed to, and what was heard in it.
///
/// <para><b>Stored, not recomputed.</b> Two reasons, and both are about the same thing. A render
/// has to be reproducible on a machine with a different ffmpeg, and audio analysis is exactly the
/// kind of thing that drifts between versions - a cue that moves by a frame between two machines
/// is the class of bug this project exists to avoid. And an agent that has read the cues once
/// should not pay to read them again on every iteration: the loop is look, adjust, look, and the
/// cues do not change between adjustments.</para>
///
/// <para><b>The hash is the point of the hash.</b> It is of the file's own bytes, so swapping the
/// track underneath a composition that was timed to it is CAUGHT rather than discovered later in
/// something that no longer lands. <c>lint</c> reports a mismatch as CUT008.</para>
/// </summary>
/// <param name="Source">What was analysed, as a file name.</param>
/// <param name="Sha256">Of the file's bytes, to catch a swap.</param>
/// <param name="Seconds">How long the track is.</param>
/// <param name="Fps">The rate the cue frame numbers are in. A cue is only actionable at the rate
/// it was snapped to, so this travels with them.</param>
/// <param name="Bpm">What the tempo search found, or 0.</param>
/// <param name="Confidence">How much that is worth, 0-1. Below 0.35 no beats were emitted.</param>
/// <param name="Asset">The key under which the track itself is stored in <c>Assets</c>, when it is.
/// Null when only the cues were kept - which is enough to animate, and not enough to mux.</param>
/// <param name="Cues">Every cue, in time order.</param>
public sealed record ProjectAudio(
    string Source,
    string Sha256,
    double Seconds,
    double Fps,
    double Bpm,
    double Confidence,
    string? Asset,
    IReadOnlyList<Cue> Cues)
{
    /// <summary>Whether the tempo was worth emitting beats for.</summary>
    public bool UsableTempo => Bpm > 0 && Confidence >= AudioCues.TempoFloor;

    public IEnumerable<Cue> Of(CueKind kind) => Cues.Where(c => c.Kind == kind);

    /// <summary>What an analysis becomes when it is written down.</summary>
    public static ProjectAudio From(AudioAnalysis analysis, string sha256, string? asset) =>
        new(analysis.Source, sha256, analysis.Seconds, analysis.Fps,
            analysis.Tempo.Bpm, analysis.Tempo.Confidence, asset, analysis.Cues);
}

/// <summary>The arguments a render would otherwise have to be told every time.</summary>
public sealed class RenderSettings
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Scale { get; set; } = 1;
    public double Fps { get; set; } = 30;

    /// <summary>How long the animation runs, in seconds. What <c>render_video</c> and
    /// <c>contact_sheet</c> use when the caller names no range.</summary>
    public double Duration { get; set; } = 3;

    /// <summary>Frame clear colour, e.g. <c>#101014</c>. Null means white.</summary>
    public string? Background { get; set; }

    public bool Alpha { get; set; }

    /// <summary>Whether elements marked <c>--cupricut-background</c> are drawn. False gives the
    /// same composition over nothing, which is what an edit wants; true gives it over its own
    /// backdrop, which is what a reviewer needs to judge the colours.</summary>
    public bool ShowBackground { get; set; } = true;

    /// <summary>Preferred codec name for <c>render_video</c>.</summary>
    public string? Codec { get; set; }

    /// <summary>The frame this renders to, when it differs from the design size above. Zero means
    /// the design size times <see cref="Scale"/>, which is what CupriCut always did. Naming one of
    /// the two is enough - the other follows from the design's aspect.</summary>
    public int OutputWidth { get; set; }

    public int OutputHeight { get; set; }

    /// <summary>How the design becomes the output frame when the two differ. Only consulted when an
    /// output size is set, because with no output size there is nothing to reconcile.</summary>
    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
}

/// <summary>Everything about the project that is not the render itself.</summary>
public sealed class ProjectMeta
{
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Which engine last wrote it - useful when a render stops matching.</summary>
    public string? Engine { get; set; }

    /// <summary>Whatever the author or the agent wants the next reader to know.</summary>
    public List<string> Notes { get; set; } = [];
}
