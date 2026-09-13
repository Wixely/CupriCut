using System.ComponentModel;
using System.Text.Json;
using CupriCut.Services;
using ModelContextProtocol.Server;

namespace CupriCut.Tools;

/// <summary>
/// What a human pointed at, and what the agent did about it.
///
/// <para>The reviewer draws a box on the studio window's preview and types a sentence. That is a
/// far better brief than prose: instead of "the logo comes in too late and sits too far left", the
/// agent gets a time, a rectangle and the sentence. These tools are how it picks that up and
/// closes it out.</para>
///
/// <para>Regions are stored normalised 0–1 of the frame, so they still mean the same thing when the
/// project is rendered at another size — but they are reported in <b>pixels at the project's own
/// render size</b>, because that is the picture the agent is about to look at.</para>
/// </summary>
[McpServerToolType]
public static class AnnotationTools
{
    [McpServerTool(Name = "list_annotations"),
     Description("""
        List the regions a reviewer marked on a project's preview, with the time, the pixel
        rectangle and what they said.

        This is the agent's brief after a human has looked at a render. Each one carries the time
        it was drawn at, so render_frame at that t to see exactly what they saw. Open ones are what
        still needs doing; pass includeResolved to see the history too.
        """)]
    public static string ListAnnotations(
        CupriCutService cut,
        [Description("Project name, with or without the .cut.json extension.")] string project,
        [Description("Include annotations already marked resolved.")] bool includeResolved = false)
    {
        var loaded = cut.LoadProject(project);
        var width = loaded.Render.Width;
        var height = loaded.Render.Height;

        var wanted = includeResolved
            ? loaded.Annotations
            : [.. loaded.OpenAnnotations];

        return JsonSerializer.Serialize(new
        {
            project = cut.ResolveProject(project, forWriting: false),
            frameSize = $"{width}x{height}",
            open = loaded.OpenAnnotations.Count(),
            total = loaded.Annotations.Count,
            annotations = wanted.OrderBy(a => a.Time).Select(a =>
            {
                var (px, py, pw, ph) = a.InPixels(width, height);
                return new
                {
                    a.Id,
                    a.Time,
                    a.Note,
                    a.Author,
                    status = a.Status.ToString(),
                    a.Resolution,
                    region = new { x = px, y = py, width = pw, height = ph },
                    normalised = new { a.X, a.Y, a.W, a.H },
                    summary = a.Describe(width, height),
                    seeIt = $"render_frame(composition: \"{Path.GetFileName(cut.ResolveProject(project, false))}\", t: {a.Time})",
                };
            }),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "resolve_annotation"),
     Description("""
        Mark an annotation as dealt with, recording what was changed.

        Do this after making the change, not instead of it - the resolution text is what the
        reviewer reads to find out what you did, and it stays in the project.
        """)]
    public static string ResolveAnnotation(
        CupriCutService cut,
        [Description("Project name, with or without the .cut.json extension.")] string project,
        [Description("The annotation's id, from list_annotations.")] string id,
        [Description("What you changed in response.")] string resolution)
    {
        Annotation? target = null;
        var updated = cut.EditProject(project, p =>
        {
            target = p.Annotations.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"No annotation '{id}' in this project. Open ids: {string.Join(", ", p.OpenAnnotations.Select(a => a.Id))}", nameof(id));
            target.Status = AnnotationStatus.Resolved;
            target.Resolution = resolution;
            target.Resolved = DateTimeOffset.UtcNow;
        });

        return JsonSerializer.Serialize(new
        {
            resolved = target!.Id,
            target.Note,
            resolution = target.Resolution,
            stillOpen = updated.OpenAnnotations.Count(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "add_annotation"),
     Description("""
        Add an annotation to a project from code, in normalised 0-1 coordinates.

        Mostly the studio window writes these when a person drags a box. An agent adds one to flag
        something for the reviewer to look at, or to leave a marker for its own next run.
        """)]
    public static string AddAnnotation(
        CupriCutService cut,
        [Description("Project name, with or without the .cut.json extension.")] string project,
        [Description("What is wrong, or what to look at.")] string note,
        [Description("Time in seconds on the composition's clock.")] double t = 0,
        [Description("Left edge, 0-1 of the frame width.")] double x = 0,
        [Description("Top edge, 0-1 of the frame height.")] double y = 0,
        [Description("Width, 0-1 of the frame width. Default 1 (the whole frame).")] double w = 1,
        [Description("Height, 0-1 of the frame height. Default 1 (the whole frame).")] double h = 1,
        [Description("Who is leaving it.")] string author = "agent")
    {
        if (string.IsNullOrWhiteSpace(note)) throw new ArgumentException("An annotation needs a note - the rectangle alone says where, not what.", nameof(note));

        var annotation = new Annotation
        {
            Time = t,
            X = x,
            Y = y,
            W = w,
            H = h,
            Note = note,
            Author = string.IsNullOrWhiteSpace(author) ? "agent" : author,
        }.Normalised();

        var updated = cut.EditProject(project, p => p.Annotations.Add(annotation));
        return JsonSerializer.Serialize(new
        {
            added = annotation.Id,
            summary = annotation.Describe(updated.Render.Width, updated.Render.Height),
            open = updated.OpenAnnotations.Count(),
        }, JsonOpts.Default);
    }

    [McpServerTool(Name = "delete_annotation"),
     Description("Remove an annotation entirely. Prefer resolve_annotation, which keeps the record of what was asked and what was done.")]
    public static string DeleteAnnotation(
        CupriCutService cut,
        [Description("Project name, with or without the .cut.json extension.")] string project,
        [Description("The annotation's id.")] string id)
    {
        var removed = 0;
        var updated = cut.EditProject(project, p => removed = p.Annotations.RemoveAll(
            a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)));

        if (removed == 0) throw new ArgumentException($"No annotation '{id}' in this project.", nameof(id));
        return JsonSerializer.Serialize(new { deleted = id, remaining = updated.Annotations.Count }, JsonOpts.Default);
    }
}
