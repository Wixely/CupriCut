using CupriCut.Services;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// A composition the engine cannot build at all.
///
/// <para><b>Why this is its own file.</b> It used to take the whole tool down. `cupricut lint
/// promo.html` died with <c>length ('-5') must be a non-negative value (Parameter 'length')</c> -
/// no file, no property, no CSS, nothing to act on - because <c>Examine</c> opened the document
/// before anything could report on it. The one thing lint exists to say was the one thing it could
/// not say.</para>
///
/// <para><b>The trigger changed when the engine was fixed, and that is the point of the file.</b>
/// It used to be <c>rgb()</c> inside a border shorthand, which threw out of CupriFace 0.26.1's
/// colour parser - <see href="https://github.com/Wixely/CupriFace/issues/196">CupriFace#196</see>,
/// where it cost a downstream corpus 35 of 187 compositions. 0.26.2 fixed it, so on 0.28.1 that
/// document builds and four tests here failed on cue.</para>
///
/// <para>The trigger is now a <c>@font-face</c> whose file is not there. That is not a bug to be
/// fixed out from under this file: a face that cannot be loaded under
/// <c>FontPolicy.RegisteredOnly</c> is an error BY DESIGN, and this tool's whole font argument is
/// that it must be. What is being tested was never the bug anyway - it is that a document the
/// engine refuses gets reported rather than taking the tool down with it.</para>
/// </summary>
public class UnbuildableTests(ITestOutputHelper output)
{
    /// <summary>A composition the engine will not build: it asks for a face that is not there,
    /// and the strict policy makes that fatal rather than a silent substitution.</summary>
    private const string Refused = """
        <div class="card" data-start="1" data-duration="2" data-cut-event="+0.5:shown">Hello</div>
        <style>
          @font-face { font-family: "Nowhere"; src: url('no-such-face.ttf'); }
          body, html { font-family: "Nowhere"; background: #101014; }
          .card { width: 300px; height: 120px; color: #f4f6fb; }
        </style>
        """;

    /// <summary>The declaration that used to bring the whole tool down, kept as a regression guard
    /// for the engine fix rather than deleted with the bug.</summary>
    private const string OnceCrashed = """
        <div class="card">Hello</div>
        <style>
          body, html { font-family: "Noto Sans"; background: #101014; }
          .card { width: 300px; height: 120px; color: #f4f6fb; border: 2px solid rgb(198, 173, 144); }
        </style>
        """;

    [Fact]
    public void Lint_survives_it_and_reports_it_as_an_error()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("refused.html", Refused);

        var x = Inspector.Examine(harness.Cut, name);

        output.WriteLine(string.Join("\n", x.Findings.Select(f => $"{f.Level} {f.Code}: {f.What}")));

        Assert.Equal("errors", x.Verdict);
        Assert.Single(x.Findings, f => f.Code == Inspector.Unbuildable);
    }

    [Fact]
    public void The_finding_names_the_family_and_the_file()
    {
        // An author who is told "the document could not be built" has nothing to do. One who is
        // told which family, and which file it wanted, has one thing to do.
        using var harness = new Harness();
        var name = harness.WriteComposition("refused.html", Refused);

        var found = Assert.Single(
            Inspector.Examine(harness.Cut, name).Findings, f => f.Code == Inspector.Unbuildable);

        Assert.Contains("Nowhere", found.What);
        Assert.Contains("no-such-face.ttf", found.What);
    }

    [Fact]
    public void A_border_with_an_rgb_colour_builds_again()
    {
        // CupriFace#196, fixed in 0.26.2. Kept as a guard rather than deleted with the advice it
        // used to justify: this one declaration cost a downstream corpus 35 of 187 compositions,
        // and a regression would be expensive to notice any other way.
        using var harness = new Harness();

        var whole = Inspector.Examine(harness.Cut,
            harness.WriteComposition("once-crashed.html", OnceCrashed));

        Assert.DoesNotContain(whole.Findings, f => f.Code == Inspector.Unbuildable);

        foreach (var (label, declaration) in new[]
                 {
                     ("longhands", "border-width:2px;border-style:solid;border-color:rgb(198, 173, 144);"),
                     ("hex", "border:2px solid #c6ad90;"),
                     ("hsl", "border:2px solid hsl(34, 35%, 66%);"),
                 })
        {
            var html = $$"""
                <div class="card">Hello</div>
                <style>
                  body, html { font-family: "Noto Sans"; background: #101014; }
                  .card { width: 300px; height: 120px; color: #f4f6fb; {{declaration}} }
                </style>
                """;

            var x = Inspector.Examine(harness.Cut, harness.WriteComposition($"{label}.html", html));

            output.WriteLine($"{label}: {x.Verdict}");
            Assert.DoesNotContain(x.Findings, f => f.Code == Inspector.Unbuildable);
        }
    }

    [Fact]
    public void What_was_read_off_the_markup_is_still_reported()
    {
        // The point of not simply failing. The timeline, the events, the backdrop and the assets
        // never needed the engine, and an author fixing the document wants to see them.
        using var harness = new Harness();
        var name = harness.WriteComposition("refused.html", Refused);

        var x = Inspector.Examine(harness.Cut, name);

        Assert.Single(x.Timeline.Windows);
        Assert.Equal(1, x.Timeline.Windows[0].Start);
        Assert.Single(x.Events.Events, e => e.Name == "shown");
        Assert.Equal(1.5, x.Events.Events.Single(e => e.Name == "shown").At, 3);
    }

    [Fact]
    public void What_the_doctor_can_still_say_is_still_said()
    {
        // The point of not simply failing: CupriDoctor reads the document whether or not this
        // tool could open it, and an author fixing one problem wants to see the others.
        using var harness = new Harness();
        var name = harness.WriteComposition("refused.html", Refused);

        var x = Inspector.Examine(harness.Cut, name);

        Assert.Equal("errors", x.Verdict);
        Assert.Single(x.Findings, f => f.Code == Inspector.Unbuildable);
    }

    [Fact]
    public void A_refusal_on_the_way_to_a_first_frame_names_what_it_refused()
    {
        // This used to open the document and assert a CompositionLoadException out of
        // CupriDocument.Load, because CupriFace#196 threw there. On 0.28.1 nothing known throws
        // there: the guard around Load is still in place and is now untriggered by any input this
        // repository can produce, which is worth saying plainly rather than testing by pretence.
        //
        // The behaviour that still matters, and that this asserts, is the one the guard exists
        // for: a refusal anywhere between opening a composition and its first frame arrives
        // NAMED. A face resolves when a layout asks for it, so this one arrives at the render.
        using var harness = new Harness();
        var name = harness.WriteComposition("refused.html", Refused);
        var loaded = harness.Cut.LoadComposition(name);

        using var doc = harness.Cut.OpenDocument(loaded);
        doc.Animate(0);

        var ex = Record.Exception(() =>
        {
            doc.Settle(1280, 720, TimeSpan.FromSeconds(10));
            using (doc.RenderToImage(1280, 720)) { }
        });

        Assert.NotNull(ex);
        output.WriteLine(ex.Message);
        Assert.Contains("Nowhere", ex.Message);
    }

    [Fact]
    public void A_composition_that_builds_is_offered_no_explanation()
    {
        // LikelyCause knows nothing at present, because the one cause it knew was fixed. The hook
        // stays: a guess about WHY the engine refused is worth having beside the refusal, and the
        // rule that it is only ever consulted after an actual failure is what keeps it from
        // accusing a working composition.
        using var harness = new Harness();
        var loaded = harness.Cut.LoadComposition(
            harness.WriteComposition("fine.html", Harness.Keyframed));

        Assert.Null(Inspector.LikelyCause(loaded));
    }

    [Fact]
    public void Every_shipped_composition_still_builds()
    {
        // The regression guard for the change itself: swallowing a load failure must not quietly
        // turn a working composition into a reported one.
        using var harness = new Harness();
        var root = Path.Combine(RepoRoot(), "compositions");

        foreach (var file in Directory.EnumerateFiles(root, "*.html").Order())
        {
            var name = Path.GetFileName(file);
            var x = Inspector.Examine(harness.Cut, harness.WriteComposition(name, File.ReadAllText(file)));

            Assert.DoesNotContain(x.Findings, f => f.Code == Inspector.Unbuildable);
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
