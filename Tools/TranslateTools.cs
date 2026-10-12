using System.ComponentModel;
using System.Text.Json;
using CupriCut.Services;
using ModelContextProtocol.Server;

namespace CupriCut.Tools;

/// <summary>
/// Browser HTML, turned into something this renderer can draw.
///
/// <para>Its own tool type rather than an extra verb on the project tools, because it is the only
/// thing here that reads a document written for a browser - everything else assumes a composition
/// an author wrote for the engine. Keeping it apart is also what makes the dependency visible: one
/// file, one tool, and nothing in the render path.</para>
/// </summary>
[McpServerToolType]
public static class TranslateTools
{
    [McpServerTool(Name = "translate"),
     Description("""
        Turn a document written for a browser into a CupriCut project.

        What this is for: ANY HTML written for a browser. A page you were sent, a template, a
        CodePen, a frame pack, an exported design, something you wrote yourself out of browser
        habit - the source does not matter. If it renders in a browser and not here, this is the
        verb to try before rewriting it by hand.

        The engine has no JavaScript at all, so a GSAP timeline is compiled into @keyframes, a raw
        <img> becomes <cupri-image>, and the rewrites CupriFace needs are applied. The result is
        saved as an ordinary project, which is then just a project - nothing consults the
        translator again when it renders.

        READ THE REFUSALS. They are the point of this tool, not a footnote. Anything motion-related
        among them means the animation is missing part of what the original did, and a silent drop
        is how someone ships a video without its transitions. An empty refusal list means the
        translation is either exact or quietly incomplete.

        Two numbers are worth reading together: `animated` is how many selectors came out with an
        animation, and `held` is how many of those hold one value for their whole length. A result
        with fifty animated and forty-eight held has carried almost no motion.

        `verdict` is CupriCut's own lint of what came out - clean, warnings or errors. The
        translator reasons about markup and never had the engine, so this is the half it could not
        answer. Expect to iterate: translate, lint, fix the CSS, render a contact sheet.

        FONT STACKS ARE REWRITTEN, and you should say so. A browser document names an operating
        system's fonts; this renderer registers faces and refuses a stack by its NAMES rather than
        by its order, so one unknown family anywhere in a list fails the whole render. Names
        nothing here can answer are dropped, which is what a browser does in effect. `fonts.trimmed`
        is routine. `fonts.unresolved` is NOT: a stack there lost everything it named, so that text
        is set in a typeface nobody chose. Put those in front of the person rather than picking one.
        """)]
    public static async Task<string> TranslateComposition(
        CupriCutService cut,
        [Description("The browser document to translate, relative to a configured composition root.")] string composition,
        [Description("Project name to save it as. Defaults to the source file's own name.")] string? project = null,
        [Description("Design width. 1280 by default.")] int width = 1280,
        [Description("Design height. 720 by default.")] int height = 720,
        [Description("Frame rate for the project's render settings. 30 by default.")] double fps = 30,
        [Description("How long the animation runs. Omit to take the compiler's own measurement of the motion, which is usually what you want.")] double? duration = null)
    {
        var t = await Translate.OfAsync(cut, composition, project, width, height, fps, duration);

        return JsonSerializer.Serialize(new
        {
            t.Source,
            t.Project,
            t.Saved,
            motion = new
            {
                t.Animated,
                t.Held,
                t.Seconds,
                carried = t.Animated - t.Held,
            },
            refusals = t.Refusals,
            refused = t.Refusals.Count,
            assets = new
            {
                inlined = t.Inlined,
                missing = t.Missing,
                external = t.External,
            },
            fonts = new
            {
                trimmed = t.DroppedFamilies,
                unresolved = t.UnresolvedStacks,
                decide = t.UnresolvedStacks.Count == 0
                    ? null
                    : "These stacks lost every family they named and now fall back to a generic, so part "
                      + "of this composition is set in a typeface nobody chose. That is a question about "
                      + "the design rather than about the markup: PUT IT TO THE PERSON rather than "
                      + "picking one. Register a face, embed one with attach_asset, or accept the generic.",
            },
            t.Verdict,
            findings = t.Findings
                .Where(f => f.Level != FindingLevel.Info)
                .Select(f => new
                {
                    level = f.Level.ToString().ToLowerInvariant(),
                    f.Code,
                    what = f.What,
                    f.Fix,
                    line = f.Line == 0 ? (int?)null : f.Line,
                }),
            note = t.Refusals.Count == 0
                ? "Nothing was refused. That means exact or quietly incomplete - compare a contact sheet against the original before trusting it."
                : "Every refusal is also written into the project's own notes, so a later session reads them without this answer.",
            next = $"contact_sheet with composition:\"{Path.GetFileName(t.Saved)}\" to see whether the motion survived.",
        }, JsonOpts.Default);
    }
}
