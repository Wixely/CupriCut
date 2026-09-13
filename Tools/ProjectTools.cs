using System.ComponentModel;
using System.Text.Json;
using CupriCut.Services;
using ModelContextProtocol.Server;

namespace CupriCut.Tools;

/// <summary>
/// Projects: the work an agent did, in a file CupriCut owns.
///
/// <para>Every other tool renders something and forgets it. These are what let a second run - a
/// different session, no shared context, maybe no filesystem tool at all - open what a first one
/// made, read the markup and the notes, change one rule and render again at the same size and rate
/// without being told any of it.</para>
///
/// <para>A saved project is a composition: pass its name to <c>render_frame</c>,
/// <c>contact_sheet</c>, <c>render_frames</c> or <c>render_video</c> exactly as you would an HTML
/// file, and the settings it carries fill in whatever you leave out.</para>
/// </summary>
[McpServerToolType]
public static class ProjectTools
{
    [McpServerTool(Name = "save_project"),
     Description("""
        Save a composition and its render settings as a self-contained .cut.json project, so a
        later run can reopen and adjust it.

        This replaces the whole project - pass update_project to change one field. The saved file
        holds the HTML, the CSS, the size/fps/duration/background that regenerate the animation,
        any inlined assets, and free-text notes for whoever reads it next. Render it by passing the
        project name wherever a composition is asked for.
        """)]
    public static string SaveProject(
        CupriCutService cut,
        [Description("Project name. The .cut.json extension is added if you leave it off.")] string name,
        [Description("The composition's HTML.")] string html,
        [Description("The composition's CSS.")] string? css = null,
        [Description("A human title for the project. Defaults to the name.")] string? title = null,
        [Description("What this animation is for.")] string? description = null,
        [Description("Frame width in CSS pixels. Default 1280.")] int width = 0,
        [Description("Frame height in CSS pixels. Default 720.")] int height = 0,
        [Description("Pixel multiplier for output. Default 1.")] int scale = 0,
        [Description("Frames per second. Default 30.")] double fps = 0,
        [Description("How long the animation runs, in seconds. Default 3.")] double duration = 0,
        [Description("Background colour, e.g. '#101014'.")] string? background = null,
        [Description("Render with a transparent background.")] bool alpha = false,
        [Description("Preferred codec for render_video: h264, h265, vp9, prores or gif.")] string? codec = null,
        [Description("Notes for whoever opens this next - intent, what was tried, what to fix.")] string[]? notes = null)
    {
        // Keep the creation stamp and the accumulated notes if this name is already taken: saving
        // over a project is a revision of it, not a different thing that happens to share a name.
        CutProject? existing = null;
        try { existing = cut.LoadProject(name); }
        catch (FileNotFoundException) { /* a new project, which is the common case */ }

        var project = new CutProject
        {
            Name = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(name) : title,
            Description = description ?? existing?.Description,
            Html = html,
            Css = css,
            Assets = existing?.Assets ?? new(StringComparer.Ordinal),
            Render = new RenderSettings
            {
                Width = width > 0 ? width : existing?.Render.Width ?? 1280,
                Height = height > 0 ? height : existing?.Render.Height ?? 720,
                Scale = scale > 0 ? scale : existing?.Render.Scale ?? 1,
                Fps = fps > 0 ? fps : existing?.Render.Fps ?? 30,
                Duration = duration > 0 ? duration : existing?.Render.Duration ?? 3,
                Background = background ?? existing?.Render.Background,
                Alpha = alpha || (existing?.Render.Alpha ?? false),
                Codec = codec ?? existing?.Render.Codec,
            },
            Meta = new ProjectMeta
            {
                Created = existing?.Meta.Created ?? DateTimeOffset.UtcNow,
                Notes = [.. existing?.Meta.Notes ?? [], .. notes ?? []],
            },
        };

        var path = cut.SaveProject(name, project);
        return JsonSerializer.Serialize(new
        {
            saved = path,
            name = project.Name,
            render = project.Render,
            assets = project.Assets.Keys,
            notes = project.Meta.Notes,
            howToRender = $"Pass composition: \"{Path.GetFileName(path)}\" to render_frame, contact_sheet, render_frames or render_video.",
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "load_project"),
     Description("""
        Read a saved project back whole: its HTML, CSS, render settings, assets and notes.

        This is the tool a fresh run calls when all it has is a name. Everything needed to
        regenerate and re-render the animation comes back in one answer, so no filesystem access
        and no memory of the session that made it are required.
        """)]
    public static string LoadProject(
        CupriCutService cut,
        [Description("Project name, with or without the .cut.json extension.")] string name,
        [Description("Leave the asset data URIs out of the answer and list only their names and sizes. They can be very large.")] bool summariseAssets = true)
    {
        var project = cut.LoadProject(name);
        return JsonSerializer.Serialize(new
        {
            path = cut.ResolveProject(name, forWriting: false),
            name = project.Name,
            project.Description,
            project.Html,
            project.Css,
            render = project.Render,
            assets = summariseAssets
                ? project.Assets.Select(a => new { name = a.Key, bytes = a.Value.Length, inline = false }).Cast<object>().ToList()
                : project.Assets.Select(a => new { name = a.Key, uri = a.Value }).Cast<object>().ToList(),
            meta = project.Meta,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "update_project"),
     Description("""
        Change named fields of a saved project, leaving everything else as it was.

        Use this rather than save_project when adjusting one thing - a CSS rule, the duration, the
        background - so the HTML does not have to be resent to change the stylesheet. Fields left
        null are untouched. Notes are appended, not replaced.
        """)]
    public static string UpdateProject(
        CupriCutService cut,
        [Description("Project name, with or without the .cut.json extension.")] string name,
        [Description("Replacement HTML. Null leaves it alone.")] string? html = null,
        [Description("Replacement CSS. Null leaves it alone.")] string? css = null,
        [Description("Replacement title.")] string? title = null,
        [Description("Replacement description.")] string? description = null,
        [Description("Replacement frame width.")] int? width = null,
        [Description("Replacement frame height.")] int? height = null,
        [Description("Replacement pixel multiplier.")] int? scale = null,
        [Description("Replacement frames per second.")] double? fps = null,
        [Description("Replacement duration in seconds.")] double? duration = null,
        [Description("Replacement background colour.")] string? background = null,
        [Description("Replacement transparency setting.")] bool? alpha = null,
        [Description("Replacement codec.")] string? codec = null,
        [Description("Notes to append.")] string[]? notes = null,
        [Description("Asset names to remove from the project.")] string[]? removeAssets = null)
    {
        var project = cut.LoadProject(name);
        var changed = new List<string>();

        if (html is not null) { project.Html = html; changed.Add("html"); }
        if (css is not null) { project.Css = css; changed.Add("css"); }
        if (title is not null) { project.Name = title; changed.Add("name"); }
        if (description is not null) { project.Description = description; changed.Add("description"); }
        if (width is { } w) { project.Render.Width = w; changed.Add("width"); }
        if (height is { } h) { project.Render.Height = h; changed.Add("height"); }
        if (scale is { } s) { project.Render.Scale = s; changed.Add("scale"); }
        if (fps is { } f) { project.Render.Fps = f; changed.Add("fps"); }
        if (duration is { } d) { project.Render.Duration = d; changed.Add("duration"); }
        if (background is not null) { project.Render.Background = background; changed.Add("background"); }
        if (alpha is { } a) { project.Render.Alpha = a; changed.Add("alpha"); }
        if (codec is not null) { project.Render.Codec = codec; changed.Add("codec"); }
        if (notes is { Length: > 0 }) { project.Meta.Notes.AddRange(notes); changed.Add("notes"); }

        foreach (var asset in removeAssets ?? [])
        {
            if (project.Assets.Remove(asset)) changed.Add($"-asset:{asset}");
        }

        var path = cut.SaveProject(name, project);
        return JsonSerializer.Serialize(new
        {
            saved = path,
            changed = changed.Count == 0 ? ["(nothing - every field was null)"] : changed,
            render = project.Render,
            notes = project.Meta.Notes,
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "list_projects"),
     Description("List the saved .cut.json projects, with their size, duration and when they were last written.")]
    public static string ListProjects(CupriCutService cut)
    {
        var root = cut.ProjectRoot;
        var projects = cut.ListProjects().Select(relative =>
        {
            try
            {
                var project = cut.LoadProject(relative);
                return (object)new
                {
                    file = relative,
                    name = project.Name,
                    project.Description,
                    size = $"{project.Render.Width}x{project.Render.Height}",
                    project.Render.Fps,
                    project.Render.Duration,
                    assets = project.Assets.Count,
                    updated = project.Meta.Updated,
                };
            }
            catch (Exception ex)
            {
                return new { file = relative, error = ex.Message };
            }
        });

        return JsonSerializer.Serialize(new { root, projects }, JsonOpts.Default);
    }

    [McpServerTool(Name = "attach_asset"),
     Description("""
        Inline a file from a composition root into a project as a data: URI, under the name the
        HTML and CSS refer to it by.

        This is what keeps a project one file. The engine resolves a data: URI anywhere it resolves
        a path, so an inlined image or font costs nothing at render time and travels with the
        project when it is copied or committed.
        """)]
    public static string AttachAsset(
        CupriCutService cut,
        [Description("Project name, with or without the .cut.json extension.")] string name,
        [Description("File to inline, relative to a composition root.")] string file,
        [Description("The name the HTML and CSS use for it, e.g. 'logo.png'. Defaults to the file's own name.")] string? asset = null)
    {
        var source = cut.ResolveRead(file);
        if (!File.Exists(source)) throw new FileNotFoundException($"No file at '{file}'.", source);

        var bytes = File.ReadAllBytes(source);
        var key = string.IsNullOrWhiteSpace(asset) ? Path.GetFileName(source) : asset;
        var uri = $"data:{MediaType(source)};base64,{Convert.ToBase64String(bytes)}";

        var project = cut.LoadProject(name);
        project.Assets[key] = uri;
        var path = cut.SaveProject(name, project);

        return JsonSerializer.Serialize(new
        {
            saved = path,
            asset = key,
            source,
            bytes = bytes.Length,
            inlineBytes = uri.Length,
            usage = $"Refer to it as src=\"{key}\" or url('{key}') in the project's HTML or CSS.",
        }, JsonOpts.Default);
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
