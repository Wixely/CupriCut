using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// A document written for a browser, turned into a project.
///
/// <para><b>What is being pinned here is the boundary, not the compiler.</b> CupriLex has its own
/// repository, its own corpus of 172 real blocks and its own regression suite, and duplicating any
/// of that here would be measuring somebody else's work with worse instruments. What this file
/// asserts is the three things that are CupriCut's to get right: that the translation arrives as a
/// project <i>this</i> repository built, that the refusals survive into the file a later session
/// opens, and that the result is linted - because the compiler never had the engine and cannot
/// know whether what it produced actually draws.</para>
///
/// <para>The third of those is not a precaution. Measured on the document below: the translator
/// refuses <b>nothing</b>, and lint then reports a <c>repeating-linear-gradient</c> that parses and
/// paints no pixels at all. Both answers are correct - the gradient is valid CSS and the compiler
/// is not a renderer - and either one alone is misleading. That is the whole argument for running
/// the two together.</para>
/// </summary>
public class TranslateTests
{
    /// <summary>A 4x4 solid red PNG, generated rather than pasted.</summary>
    private const string RedDot =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGN4oKAARwzEcQDgQxIBzrTn9gAAAABJRU5ErkJggg==";

    /// <summary>A block in the shape the corpus actually comes in: design tokens as custom
    /// properties, a raw <c>&lt;img&gt;</c>, a repeating gradient, and every bit of its motion in a
    /// GSAP timeline rather than in CSS.</summary>
    private const string BrowserBlock = """
        <!doctype html>
        <html><head><style>
          :root { --brand: #d9642a; --ink: #0f131a; }
          body { margin:0; width:1280px; height:720px; background:var(--ink); font-family:"Noto Sans"; }
          .card { position:absolute; left:80px; top:260px; width:520px; padding:28px; background:var(--brand); }
          .title { font-size:44px; font-weight:700; color:#ffffff; }
          .stripe { width:520px; height:8px;
                    background:repeating-linear-gradient(90deg,#fff 0 10px,#000 10px 20px); }
        </style></head>
        <body>
          <div class="card">
            <div class="title">MATCHDAY</div>
          </div>
          <div class="stripe"></div>
          <script src="https://cdn.jsdelivr.net/npm/gsap@3/dist/gsap.min.js"></script>
          <script>
            const tl = gsap.timeline();
            tl.from(".card", { duration: 0.8, opacity: 0, x: -60, ease: "power2.out" });
            tl.from(".title", { duration: 0.6, opacity: 0, y: 24 }, 0.3);
            tl.to(".stripe", { duration: 1.2, scaleX: 1.4 }, 0.9);
          </script>
        </body></html>
        """;

    [Fact]
    public async Task A_gsap_timeline_becomes_motion_in_a_saved_project()
    {
        using var harness = new Harness();
        var source = harness.WriteComposition("hero.html", BrowserBlock);

        var t = await Translate.OfAsync(harness.Cut, source);

        // Three tweens in, three animations out, and none of them held - a held animation keeps one
        // value for its whole length, so counting them apart is what stops "fifty animations" from
        // describing a translation that carried no movement.
        Assert.Equal(3, t.Animated);
        Assert.Equal(0, t.Held);
        Assert.True(t.Seconds > 0, "the compiler should report how long the motion runs");

        // It is a project of ours, loadable by the ordinary reader, with the motion's own measured
        // length as its duration rather than the 3s default.
        var project = harness.Cut.LoadProject(t.Project);
        Assert.Equal(t.Seconds, project.Render.Duration);
        Assert.Contains("@keyframes", project.Html);

        // No JavaScript reaches the engine, because there is no runtime for it to reach: neither
        // the inline timeline nor the CDN tag that would have loaded the library.
        Assert.DoesNotContain("<script", project.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gsap.timeline", project.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jsdelivr", project.Html, StringComparison.OrdinalIgnoreCase);

        // The word GSAP does survive, in a comment the compiler writes over the keyframes it
        // emitted. Asserted rather than merely tolerated: a stylesheet that cannot say where its
        // motion came from is one nobody can re-derive, and Milestone 5 wants the provenance kept
        // for a reason that outlives the licence - a project that cannot name its source is a
        // project nobody can re-license later.
        Assert.Contains("Compiled from GSAP", project.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lint_catches_what_the_translator_did_not_refuse()
    {
        // The pair that justifies linting the output rather than trusting the refusals. The
        // compiler reasons about markup and is right about it; the gradient below is valid CSS,
        // so there is nothing for it to refuse. It also paints nothing in this engine, which only
        // something holding the engine can say.
        using var harness = new Harness();
        var source = harness.WriteComposition("hero.html", BrowserBlock);

        var t = await Translate.OfAsync(harness.Cut, source);

        Assert.Empty(t.Refusals);
        Assert.Contains(t.Findings, f => f.Code == "CF0051");
        Assert.NotEqual("clean", t.Verdict);
    }

    [Fact]
    public async Task The_refusals_are_written_into_the_project_a_later_session_opens()
    {
        // A template is the case that produces one reliably: its contents are inert in a browser
        // until a host clones them in, and there is no host here.
        using var harness = new Harness();
        var source = harness.WriteComposition("templated.html",
            """
            <html><body>
              <template><div class="late">arrives by script</div></template>
              <div class="here">present</div>
            </body></html>
            """);

        var t = await Translate.OfAsync(harness.Cut, source);

        Assert.NotEmpty(t.Refusals);

        // The answer is gone by the next session; the file is not. Every refusal is in the notes,
        // which is the field a second run reads to find out what the first one was dealing with.
        var project = harness.Cut.LoadProject(t.Project);
        foreach (var refusal in t.Refusals)
            Assert.Contains(project.Meta.Notes, note => note.Contains(refusal, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_translation_that_refused_nothing_says_so_in_the_file()
    {
        // The compiler's own warning, kept where it is useful: an empty refusal list means exact
        // or quietly incomplete, and a later reader of the project needs to know which question to
        // ask rather than reading the silence as a pass.
        using var harness = new Harness();
        var source = harness.WriteComposition("hero.html", BrowserBlock);

        var t = await Translate.OfAsync(harness.Cut, source);
        Assert.Empty(t.Refusals);

        var project = harness.Cut.LoadProject(t.Project);
        Assert.Contains(project.Meta.Notes,
            note => note.Contains("quietly incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_system_font_stack_is_trimmed_to_what_this_installation_can_answer()
    {
        // WITHOUT this, translating almost any real browser document produced a project that could
        // not be rendered AT ALL. Measured before the trim went in: this exact document translated
        // cleanly, refused nothing, and then failed with
        // "FontNotRegisteredException: No registered font covers the family 'Segoe UI'".
        //
        // Two reasonable decisions collide to cause it. CupriCut sets FontPolicy.RegisteredOnly
        // always, so a render is reproducible rather than dependent on what this machine has
        // installed. And the engine answers a stack by its NAMES, not by its order: one unknown
        // name anywhere refuses the whole declaration, even with a registered family and a generic
        // after it. Most of the web names an operating system's fonts, so most of the web is fatal
        // here rather than merely unlucky.
        using var harness = new Harness();
        var source = harness.WriteComposition("fonts.html",
            """
            <html><head><style>
              body { width:1280px; height:720px; background:#0f131a; }
              .title { font-family: "Segoe UI", "Noto Sans", sans-serif; font-size:48px; color:#fff; }
            </style></head>
            <body><div class="title">MATCHDAY</div></body></html>
            """);

        var t = await Translate.OfAsync(harness.Cut, source);

        // Segoe UI goes, because nothing here answers it. Noto Sans STAYS, because this
        // installation has registered it - which is the half CupriLex cannot do for itself, since
        // it writes a package without knowing which renderer will open it.
        Assert.Contains("Segoe UI", t.DroppedFamilies.Keys);
        Assert.Contains("Noto Sans", harness.Cut.LoadProject(t.Project).Html);

        // And the whole point: it renders now.
        Assert.Equal("clean", t.Verdict);
        Assert.Empty(t.UnresolvedStacks);
    }

    [Fact]
    public async Task A_stack_that_loses_everything_is_raised_as_a_decision_not_resolved()
    {
        // The other half, and the one an agent must not answer on somebody's behalf. Neither this
        // tool nor the compiler will pick a typeface nobody asked for: the choice belongs to
        // whoever is publishing the composition, and flagging it is the entire point.
        using var harness = new Harness();
        var source = harness.WriteComposition("brand.html",
            """
            <html><head><style>
              body { width:1280px; height:720px; background:#0f131a; }
              .title { font-family: "Segoe UI", "Helvetica Neue"; font-size:48px; color:#fff; }
            </style></head>
            <body><div class="title">MATCHDAY</div></body></html>
            """);

        var t = await Translate.OfAsync(harness.Cut, source);

        var stack = Assert.Single(t.UnresolvedStacks);
        Assert.Contains("Segoe UI", stack);

        // Written into the project too, because the answer is gone by the next session and the
        // file is what that session opens.
        Assert.Contains(harness.Cut.LoadProject(t.Project).Meta.Notes,
            n => n.StartsWith("DECIDE:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_files_beside_the_source_travel_inside_the_project()
    {
        // The founding property of a project, and `translate` shipped without it. Measured on the
        // first version: a document beside a logo.png produced a project with ZERO assets and
        // `src="file:///C:/Users/.../logo.png"` in its markup - a project that rendered perfectly
        // on one computer, in one directory, and nowhere else.
        //
        // The cause was handing the compiler the source directory, which makes it rebase every
        // relative reference to an absolute file:/// URL. Correct for a renderer about to run
        // here; wrong for a file meant to be copied. Left relative and inlined instead.
        using var harness = new Harness();
        File.WriteAllBytes(Path.Combine(harness.Compositions, "logo.png"),
            Convert.FromBase64String(RedDot));
        var source = harness.WriteComposition("card.html",
            """
            <html><head><style>
              .mark { background-image:url('logo.png'); width:64px; height:64px; }
            </style></head>
            <body><cupri-image src="logo.png" style="width:64px;height:64px"></cupri-image>
            <div class="mark"></div></body></html>
            """);

        var t = await Translate.OfAsync(harness.Cut, source);

        Assert.Equal(["logo.png"], t.Inlined);

        var project = harness.Cut.LoadProject(t.Project);
        Assert.StartsWith("data:image/png;base64,", project.Assets["logo.png"]);

        // The markup still says what the author said. Both forms of it.
        Assert.Contains("src=\"logo.png\"", project.Html);
        Assert.Contains("url('logo.png')", project.Html);

        // And nothing points at this machine.
        Assert.DoesNotContain("file:///", project.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_web_font_is_reported_rather_than_fetched_unless_it_is_allowed()
    {
        // Cut:AllowWebFonts is off by default and stays off here, so this test makes no network
        // request - which is also the point being asserted. A renderer that reaches the internet
        // because a document mentioned a hostname is a different kind of program from one that
        // does not, and that difference should not be decided by a document.
        //
        // What it must NOT do is go quiet. With the face unavailable the family resolves to
        // nothing, so the stack is trimmed and the heading falls back to a generic. Somebody has
        // to be told why, or they are left wondering where their typeface went.
        using var harness = new Harness();
        Assert.False(harness.Cut.Options.AllowWebFonts);

        var source = harness.WriteComposition("webfont.html",
            """
            <html><head>
            <link href="https://fonts.googleapis.com/css2?family=Inter:wght@700" rel="stylesheet">
            <style>.title{font-family:"Inter",sans-serif;font-size:48px;color:#fff;}</style>
            </head><body><div class="title">MATCHDAY</div></body></html>
            """);

        var t = await Translate.OfAsync(harness.Cut, source);

        var request = Assert.Single(t.External);
        Assert.Contains("fonts.googleapis.com", request);
        Assert.Contains("not fetched", request);

        // Nothing was downloaded, so Inter is gone and that is raised as a decision.
        Assert.Contains("Inter", t.DroppedFamilies.Keys);
        Assert.NotEmpty(t.UnresolvedStacks);

        // And it is in the file, because the answer is gone by the next session.
        Assert.Contains(harness.Cut.LoadProject(t.Project).Meta.Notes,
            n => n.StartsWith("External:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_outside_the_read_roots_is_refused()
    {
        // The translator is an agent-facing tool, so it is bounded by the same roots as every
        // other one. A human dragging a file onto the window is a different act and takes a
        // different path - see StudioController and FileDropTests.
        using var harness = new Harness();
        var outside = Path.Combine(Path.GetTempPath(), $"cupricut-outside-{Guid.NewGuid():N}.html");
        File.WriteAllText(outside, BrowserBlock);

        try
        {
            await Assert.ThrowsAsync<CutPolicyException>(() => Translate.OfAsync(harness.Cut, outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
