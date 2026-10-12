// Aliases rather than a using of the whole namespace, and on purpose: CupriLex.Compiler exports
// its own `Composition` record, which would sit in scope beside this namespace's `Composition` and
// make every mention of the word ambiguous. Naming each type that is actually wanted keeps the
// clash from ever arising, here or in anything that copies this file's header. The list grows
// slowly, which is a fair price for never having to disambiguate the most-used word in the
// codebase.
using AngleSharp;
using AngleSharp.Html.Parser;
using External = CupriLex.Compiler.External;
using Fallbacks = CupriLex.Compiler.Fallbacks;
using Fetches = CupriLex.Compiler.Fetches;
using Http = CupriLex.Compiler.Http;
using Translated = CupriLex.Compiler.Translated;
using Translator = CupriLex.Compiler.Translator;
using WebFonts = CupriLex.Compiler.WebFonts;

namespace CupriCut.Services;

/// <summary>What a translation produced, and everything it could not carry.</summary>
/// <param name="Source">The file that was read.</param>
/// <param name="Project">The project name it was saved as.</param>
/// <param name="Saved">Where it landed.</param>
/// <param name="Animated">How many selectors came out with an animation on them.</param>
/// <param name="Held">How many of those hold one value for their whole length. Emitted because
/// their end state is load-bearing, and not motion - a number worth separating, because a
/// translation that reports fifty animations and forty-eight held ones has not carried much.</param>
/// <param name="Seconds">How long the motion runs, as the compiler measured it.</param>
/// <param name="Refusals">Everything that did not come across, each in the author's own terms and
/// with its line where that is known. <b>The most important field here.</b></param>
/// <param name="Verdict">What CupriCut's own <c>lint</c> makes of the result: clean, warnings or
/// errors. The translator cannot know this - it does not have the engine.</param>
/// <param name="Findings">That verdict's findings, so the caller does not have to lint again.</param>
/// <param name="DroppedFamilies">Font families removed from the stacks because nothing here can
/// answer them, with how many declarations named each. Ordinary and expected - these are an
/// operating system's fonts, which a browser only ever used because it happened to be running on
/// the machine that had them.</param>
/// <param name="UnresolvedStacks">Stacks that lost <b>every</b> name they had and fell back to a
/// bare generic. <b>This is the one to put in front of a person.</b> It means the composition is
/// now set in a typeface nobody chose, which is a question about their brand rather than about
/// their markup - and neither this tool nor the compiler will answer it by picking something.</param>
/// <param name="Inlined">Files from beside the source that now travel inside the project, by the
/// name its markup uses.</param>
/// <param name="Missing">Sources the markup names that were not beside it. Still named in the
/// markup, so <c>attach_asset</c> under the same name fills each one in.</param>
/// <param name="External">Every font or stylesheet the document wanted from the network, and what
/// came of it. With <c>Cut:AllowWebFonts</c> off these all read "not fetched", which is the line to
/// show someone who is wondering where their typeface went.</param>
public sealed record Translation(
    string Source,
    string Project,
    string Saved,
    int Animated,
    int Held,
    double Seconds,
    IReadOnlyList<string> Refusals,
    string Verdict,
    IReadOnlyList<Finding> Findings,
    IReadOnlyDictionary<string, int> DroppedFamilies,
    IReadOnlyList<string> UnresolvedStacks,
    IReadOnlyList<string> Inlined,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> External);

/// <summary>
/// Browser HTML in, a CupriCut project out.
///
/// <para><b>Why the dependency, and what it is a dependency on.</b> Milestone 5 left exactly one
/// question for this repository: take <c>CupriLex.Compiler</c> as a package or keep the two at
/// arm's length. It is taken - for the compiler, and for nothing else.</para>
///
/// <para><b>Why its packaging is not used, which is a different question from whether it works.</b>
/// <c>.cutpkg</c> is <i>this repository's</i> format. CupriLex wrote one because that was the only
/// way to hand over a translation with its assets attached - the downloaded faces, the images
/// beside the block - and it had to re-implement the schema from a hand-copied DTO to do it,
/// because depending on CupriCut would mean the compiler needed a renderer in order to build. That
/// was a reasonable trade while nothing on this side could do the bundling.</para>
///
/// <para>Now something can: this file. The assets are gathered here, the faces are fetched here,
/// and <see cref="CutProject"/> and <see cref="CutPackage"/> are written by the code that owns
/// them. So the translation crosses the boundary as what it actually is - markup, a stylesheet and
/// a list of refusals - and nobody keeps a second copy of a schema they do not own.</para>
///
/// <para><b>The direction of travel is unchanged.</b> A composition is translated ONCE, into a
/// project that was already self-contained, and nothing consults the translator at render time. A
/// project that has been imported is just a project. That was the mitigation for the honest risk
/// Milestone 5 names - a maintenance surface tracking a project we do not control - and taking the
/// reference does not touch it: the renderer has no idea this type exists.</para>
///
/// <para><b>The report is the product.</b> Silently dropping a shader transition is how someone
/// ships a video missing its transitions, so the refusals are returned, and they are also written
/// into the project's own notes - because the file is what a second run opens, and a refusal the
/// next session cannot see is a refusal nobody acts on. The compiler's own warning about its
/// report is worth repeating: an empty refusal list means perfect or lying.</para>
///
/// <para><b>And then it is linted.</b> The compiler reasons about markup; it does not have the
/// engine, so it cannot know whether the thing it produced actually draws. That is precisely what
/// this repository's <c>lint</c> is for, and running it here means one answer covers both halves
/// rather than leaving the caller to discover the second one later.</para>
/// </summary>
public static class Translate
{
    /// <summary>Everything a translation needs, and the whole of it in one call - the house rule
    /// that every tool and every CLI verb shares one function rather than reimplementing it.</summary>
    /// <param name="cut">The service, for the read roots and the project root.</param>
    /// <param name="file">The source document, under a composition root.</param>
    /// <param name="project">What to save it as. Defaults to the source file's own name.</param>
    /// <param name="width">Design width for the project's render settings.</param>
    /// <param name="height">Design height.</param>
    /// <param name="fps">Frame rate.</param>
    /// <param name="duration">How long to render. Null takes the compiler's own measurement of the
    /// motion, which is the whole reason it reports one.</param>
    public static async Task<Translation> OfAsync(
        CupriCutService cut,
        string file,
        string? project = null,
        int width = 1280,
        int height = 720,
        double fps = 30,
        double? duration = null,
        CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(cut);

        // The agent-facing path, so it is bounded by the read roots like every other tool that
        // takes a path. A human dragging a file onto the window is a different story and is
        // handled elsewhere - see StudioController.OnFileDropped - because a person pointing at
        // their own file is not the same act as a request arriving over a socket.
        var source = cut.ResolveRead(file);
        if (!File.Exists(source))
            throw new CutPolicyException($"No file at '{file}'.");

        var html = File.ReadAllText(source);

        var directory = Path.GetDirectoryName(source) ?? ".";

        Translated translated;
        try
        {
            // NO DIRECTORY, deliberately. Given one, the compiler rebases every relative reference
            // to an absolute file:/// URL - correct for a renderer about to run on this machine,
            // and wrong for the thing being built here. Measured: translating a document beside a
            // logo.png produced a project with ZERO assets and
            // `src="file:///C:/Users/.../compositions/bundle/logo.png"` in its markup. It rendered
            // perfectly on this computer and nowhere else, which breaks the one promise a project
            // makes - self-contained, copyable, hand it to another machine.
            //
            // Left relative, the references stay as the author wrote them and Bundle.Beside below
            // inlines them under those same names. Same markup, bytes inside the file.
            translated = Translator.Of(html, directory: null);
        }
        catch (Exception ex)
        {
            // A document the compiler cannot read is a refusal, not a crash - the same bargain
            // OpenDocument makes for a document the engine cannot build.
            throw new CutPolicyException(
                $"CupriLex could not translate '{Path.GetFileName(source)}': "
                + $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
        }

        // ---- font stacks ---------------------------------------------------------------------
        //
        // Without this, translating almost any real browser document produces a project that
        // CANNOT BE RENDERED AT ALL. Measured: a title styled
        // `font-family: "Segoe UI", system-ui, -apple-system, sans-serif` translated cleanly,
        // refused nothing, and then failed to build with FontNotRegisteredException naming
        // "Segoe UI".
        //
        // The cause is the interaction of two reasonable decisions. CupriCut sets
        // FontPolicy.RegisteredOnly always, because a family answered by whatever this machine
        // happens to have installed is the difference between a render CI reproduces and one it
        // merely resembles. And the engine answers a stack by its NAMES rather than by its order:
        // one unknown name anywhere in the list refuses the whole declaration, even with a
        // registered family and a generic after it. So every stack naming an operating system's
        // fonts - which is most of the web - is fatal here rather than merely unlucky.
        //
        // Dropping the names nothing can answer is what a browser does in effect: it walks the
        // list and uses the first face it has. Writing that down makes the choice explicit and the
        // render reproducible, which is the same argument the strict policy is making.
        var document = new HtmlParser().ParseDocument(translated.Html);

        // ---- the faces the document does not carry ---------------------------------------------
        //
        // BEFORE the trim, because a face that arrives here is a family the trim must then keep.
        // Gathering rewrites the document's <link> to a font service into local @font-face rules,
        // so Fallbacks.Carried below sees it.
        //
        // Off by default - this is the only outbound request this tool makes, and a renderer that
        // reaches the internet because a document mentioned a hostname is a different kind of
        // program from one that does not. Off, the requests are still REPORTED, because the
        // alternative is somebody wondering where their typeface went.
        var fetched = new Dictionary<string, string>(StringComparer.Ordinal);
        var wanted = new List<string>();

        if (cut.Options.AllowWebFonts)
        {
            using var http = new Http();
            var gathered = await WebFonts.GatherAsync(document, WebFontConsent.Instance, http, cancel)
                .ConfigureAwait(false);

            foreach (var face in gathered.Faces)
                fetched[face.Key] = Bundle.DataUri(face.Bytes, CutPackage.MediaTypeOf(face.Key));

            // Every request and what came of it, including the ones that failed - a face that did
            // not arrive is about to become a trimmed family, and the two lines belong together.
            wanted.AddRange(gathered.Requested.Select(r => $"{r.Request.Url} - {r.Outcome}"));
        }
        else
        {
            // Listed without fetching anything, so the answer can say what turning the option on
            // would get. External.Of reads the document; it makes no request.
            wanted.AddRange(External.Of(document)
                .Where(r => r.Kind is Fetches.Font or Fetches.Stylesheet)
                .Select(r => $"{r.Url} - not fetched (Cut:AllowWebFonts is off)"));
        }

        // The document's own @font-face families PLUS what this installation has registered. The
        // second half is the part CupriLex cannot do for itself: it writes a package without
        // knowing which renderer will open it, so it can only count what travels inside the file.
        // CupriCut is the renderer, and knows. That is the difference between keeping Noto Sans in
        // a stack and throwing it away as unanswerable.
        var answerable = Fallbacks.Carried(document);
        foreach (var family in cut.RegisteredFamilies()) answerable.Add(family);

        var dropped = Fallbacks.Trim(document, answerable);
        var trimmed = document.ToHtml();

        // ---- the assets ----------------------------------------------------------------------
        //
        // The same inlining the studio's drop import does, for the same reason: a project is only
        // worth the name if it is the self-contained file this repository promises. Keyed by the
        // source string as written, so no markup changes hands.
        var bundled = Bundle.Beside(trimmed, css: null, directory,
            already: new HashSet<string>(fetched.Keys, StringComparer.Ordinal));

        // The fetched faces travel the same way the local files do, so nothing downstream knows or
        // cares which arrived from disk and which from a font service.
        var assets = new Dictionary<string, string>(bundled.Assets, StringComparer.Ordinal);
        foreach (var (key, uri) in fetched) assets[key] = uri;

        var name = string.IsNullOrWhiteSpace(project)
            ? Path.GetFileNameWithoutExtension(source)
            : project;

        // The motion's own length, unless the caller named one. Rounded, because a duration is
        // read by people and three decimal places of it never told anyone anything.
        var seconds = Math.Round(translated.Motion.Seconds, 3);
        var refusals = translated.Refusals.Select(r => r.ToString()).ToList();

        var saved = new CutProject
        {
            Name = name,
            Description = $"Translated from {Path.GetFileName(source)} by CupriLex.",
            // The keyframes the compiler emitted are already inside this document's own <style> -
            // it injects them before handing the markup back - so there is no separate stylesheet
            // to carry and Css stays null rather than holding a duplicate.
            Html = trimmed,
            Render = new RenderSettings
            {
                Width = width,
                Height = height,
                Fps = fps,
                Duration = duration ?? (seconds > 0 ? seconds : 3),
            },
            Assets = assets,
        };

        saved.Meta.Engine = CupriCutService.EngineVersion;
        saved.Meta.Notes.Add($"Translated from {Path.GetFileName(source)} by CupriLex "
            + $"{typeof(Translator).Assembly.GetName().Version}.");
        saved.Meta.Notes.Add(refusals.Count == 0
            // Said in the file rather than only in the answer, because this is the sentence a
            // second session needs and the answer will be long gone by then.
            ? "The translator refused nothing. That means the translation is either exact or "
              + "quietly incomplete; compare it against the original before trusting it."
            : $"The translator could not carry {refusals.Count} thing(s). Each is listed below, "
              + "and anything motion-related among them means this animation is missing part of "
              + "what the original did.");
        foreach (var refusal in refusals) saved.Meta.Notes.Add("Refused: " + refusal);

        if (assets.Count > 0)
            saved.Meta.Notes.Add(
                $"Inlined {assets.Count} asset(s), so this project carries them: "
                + $"{string.Join(", ", assets.Keys)}.");

        foreach (var request in wanted) saved.Meta.Notes.Add("External: " + request);

        if (bundled.Missing.Count > 0)
            saved.Meta.Notes.Add(
                $"{bundled.Missing.Count} source(s) were not next to the document and are NOT in this "
                + $"project: {string.Join(", ", bundled.Missing)}. The markup still names them, so "
                + "attach_asset under the same name fills each one in.");

        if (dropped.Families.Count > 0)
            saved.Meta.Notes.Add(
                $"Trimmed {dropped.Families.Count} font famil(y/ies) out of the stacks, because nothing "
                + "registered here answers them and the strict font policy refuses a stack by its NAMES "
                + $"rather than by its order: {string.Join(", ", dropped.Families.Keys)}.");

        foreach (var stack in dropped.Unresolved)
            saved.Meta.Notes.Add(
                $"DECIDE: the stack '{stack.Stack}' lost every family it named and now falls back to a "
                + $"generic, so {stack.Declarations} declaration(s) are set in a typeface nobody chose. "
                + "Register a face for one of them, embed one with attach_asset, or accept the generic.");

        var path = cut.SaveProject(name, saved);

        // CupriCut's own verdict on what came out. The compiler never had the engine, so this is
        // the half of the answer it could not have given.
        var examined = Inspector.Examine(cut, path);

        return new Translation(
            Source: source,
            Project: name,
            Saved: path,
            Animated: translated.Motion.Elements,
            Held: translated.Motion.Held,
            Seconds: seconds,
            Refusals: refusals,
            Verdict: examined.Verdict,
            Findings: examined.Findings,
            DroppedFamilies: dropped.Families,
            UnresolvedStacks: [.. dropped.Unresolved.Select(u => u.Stack)],
            Inlined: [.. assets.Keys],
            Missing: bundled.Missing,
            External: wanted);
    }
}
