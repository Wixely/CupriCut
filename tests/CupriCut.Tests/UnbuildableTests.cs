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
/// <para>The trigger is measured, not guessed: <c>rgb()</c> or <c>rgba()</c> inside a border
/// shorthand throws out of CupriFace 0.26.1's colour parser.
/// <see href="https://github.com/Wixely/CupriFace/issues/196">CupriFace#196</see>, where it costs a
/// downstream corpus 35 of 187 compositions.</para>
/// </summary>
public class UnbuildableTests(ITestOutputHelper output)
{
    private const string Crashes = """
        <div class="card" data-start="1" data-duration="2" data-cut-event="+0.5:shown">Hello</div>
        <style>
          body, html { font-family: "Noto Sans"; background: #101014; }
          .card { width: 300px; height: 120px; color: #f4f6fb; border: 2px solid rgb(198, 173, 144); }
        </style>
        """;

    [Fact]
    public void Lint_survives_it_and_reports_it_as_an_error()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("crash.html", Crashes);

        var x = Inspector.Examine(harness.Cut, name);

        output.WriteLine(string.Join("\n", x.Findings.Select(f => $"{f.Level} {f.Code}: {f.What}")));

        Assert.Equal("errors", x.Verdict);
        Assert.Single(x.Findings, f => f.Code == Inspector.Unbuildable);
    }

    [Fact]
    public void The_advice_names_the_declaration_and_the_way_out()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("crash.html", Crashes);

        var found = Assert.Single(
            Inspector.Examine(harness.Cut, name).Findings, f => f.Code == Inspector.Unbuildable);

        Assert.Contains("border: 2px solid rgb(198, 173, 144)", found.Fix);
        Assert.Contains("border-color", found.Fix);
        Assert.Contains("196", found.Fix);
    }

    [Fact]
    public void Every_alternative_the_advice_offers_actually_works()
    {
        // The test that matters most. Advice that does not work is worse than none - an author
        // who follows it and still fails has been sent in a circle. Each of these is rendered.
        using var harness = new Harness();

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
        var name = harness.WriteComposition("crash.html", Crashes);

        var x = Inspector.Examine(harness.Cut, name);

        Assert.Single(x.Timeline.Windows);
        Assert.Equal(1, x.Timeline.Windows[0].Start);
        Assert.Single(x.Events.Events, e => e.Name == "shown");
        Assert.Equal(1.5, x.Events.Events.Single(e => e.Name == "shown").At, 3);
    }

    [Fact]
    public void The_engines_own_verdict_comes_through_too()
    {
        // CupriDoctor survives this - it always did - and reports CF0001. Lint simply never
        // reached it, because opening the document came first.
        using var harness = new Harness();
        var name = harness.WriteComposition("crash.html", Crashes);

        Assert.Contains(Inspector.Examine(harness.Cut, name).Findings, f => f.Code == "CF0001");
    }

    [Fact]
    public void A_render_refuses_by_name_rather_than_throwing_from_a_css_parser()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("crash.html", Crashes);
        var loaded = harness.Cut.LoadComposition(name);

        var ex = Assert.Throws<CompositionLoadException>(() => harness.Cut.OpenDocument(loaded));

        output.WriteLine(ex.Message);
        Assert.Contains("crash.html", ex.Message);
        Assert.Contains("border-color", ex.Message);
        Assert.IsType<ArgumentOutOfRangeException>(ex.Engine);
    }

    [Fact]
    public void A_composition_that_builds_is_offered_no_explanation()
    {
        // The cause is only ever consulted when a build actually failed, which is what stops it
        // accusing a perfectly good composition. The day CupriFace#196 is fixed, this file's other
        // tests will fail and this one will not - and that is the right way round.
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
