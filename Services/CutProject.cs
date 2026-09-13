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

    public static bool IsProjectPath(string path) =>
        path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Preferred codec name for <c>render_video</c>.</summary>
    public string? Codec { get; set; }
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
