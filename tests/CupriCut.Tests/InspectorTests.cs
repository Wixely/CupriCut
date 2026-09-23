using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// One look at a composition, asked two ways.
///
/// <para><c>inspect</c> wants the structure and <c>lint</c> wants the verdict, and both come from
/// here so they cannot drift about what they found.</para>
///
/// <para>The checks are run against the composition's OWN frame and with a non-null stylesheet,
/// and both of those are load-bearing rather than tidy: the doctor defaults to a 1024x768 viewport,
/// which reports a 1280-wide composition as overflowing for being 1280 wide; and passing null as
/// the stylesheet turns the CSS pass off entirely, inline <c>&lt;style&gt;</c> included
/// (CupriFace#183). Both made this tool confidently wrong before they were found.</para>
/// </summary>
public sealed class InspectorTests
{
    private const string Clean = """
        <div class="card">Quarterly review</div>
        <style>
          body, html { margin:0; font-family:"Noto Sans"; }
          .card { width:200px; height:100px; background-color:#d9642a; animation: rise 1s linear both; }
          @keyframes rise { from { opacity:0; } to { opacity:1; } }
        </style>
        """;

    [Fact]
    public void A_sound_composition_is_clean()
    {
        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("clean.html", Clean));

        Assert.Equal("clean", x.Verdict);
        Assert.Equal(0, x.Errors);
        Assert.Equal(0, x.Warnings);
        Assert.True(x.Settled);
        Assert.True(x.Purity.PureInTime);
    }

    [Fact]
    public void The_fonts_it_asked_for_and_what_answered()
    {
        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("clean.html", Clean));

        // A layout is what asks for a family, so a composition with no TEXT resolves nothing -
        // which is why this one has words in it.
        var font = Assert.Single(x.Fonts);
        Assert.Equal("Noto Sans", font.Asked);
        // Registered rather than resolved from the machine - the whole point of the strict policy.
        Assert.False(font.MachineDependent);
        Assert.Contains("noto sans", x.RegisteredFamilies, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_timeline_comes_back_as_data()
    {
        const string Html = """
            <div class="one" data-start="0" data-duration="2" data-track="main"></div>
            <div class="two" data-start="2" data-duration="3" data-track="overlay"></div>
            <style>body,html{font-family:"Noto Sans";} .one,.two{width:10px;height:10px;}</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("t.html", Html));

        Assert.Equal(2, x.Timeline.Windows.Count);
        Assert.Equal(["main", "overlay"], x.Timeline.Tracks);
        Assert.Equal(5, x.Timeline.Duration);
        Assert.Equal(5, x.Duration);
        // The generated class is CupriCut's business, not something to report back at the author.
        Assert.Equal("one", x.Timeline.Windows[0].Classes);
    }

    [Fact]
    public void An_unsupported_css_property_inside_a_style_block_is_found()
    {
        // The case that was silently passing: a plain .html composition keeps its rules in <style>,
        // and null as the stylesheet argument turns the CSS pass off completely.
        const string Html = """
            <div class="a"></div>
            <style>body,html{font-family:"Noto Sans";} .a { text-transform: uppercase; width:10px; height:10px; }</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("ls.html", Html));

        Assert.Contains(x.Findings, f => f.Code == "CF0050" && f.What.Contains("text-transform"));
        Assert.Equal("warnings", x.Verdict);
    }

    [Fact]
    public void An_element_the_engine_cannot_draw_is_an_error()
    {
        const string Html = """
            <img src="logo.png">
            <style>body,html{font-family:"Noto Sans";}</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("img.html", Html));

        Assert.Equal("errors", x.Verdict);
        Assert.Contains(x.Findings, f => f.Level == FindingLevel.Error && f.Code == "CF0030");
    }

    [Fact]
    public void A_timeline_problem_reaches_the_findings()
    {
        const string Html = """
            <div class="scene" data-start="1"></div>
            <style>body,html{font-family:"Noto Sans";} .scene { width:10px; height:10px; animation: fade 1s linear both; }
            @keyframes fade { from{opacity:0;} to{opacity:1;} }</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("clash.html", Html));

        Assert.Contains(x.Findings, f => f.Code == Inspector.TimelineProblem);
    }

    [Fact]
    public void Being_impure_is_information_rather_than_a_fault()
    {
        // Correct, and slower. A verdict of "errors" for a composition that renders exactly what
        // its author meant would be a lie.
        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("t.html", Harness.Transitioned));

        Assert.False(x.Purity.PureInTime);
        var note = Assert.Single(x.Findings, f => f.Code == Inspector.Impure);
        Assert.Equal(FindingLevel.Info, note.Level);
        Assert.Equal("clean", x.Verdict);
    }

    [Fact]
    public void A_composition_is_checked_at_its_own_frame_not_the_doctors_default()
    {
        // 1200px of content is fine in a 1280 frame and overflows the doctor's 1024 default. Asking
        // about the wrong viewport reported a correct composition as broken.
        const string Html = """
            <div class="wide"></div>
            <style>body,html{margin:0;font-family:"Noto Sans";} .wide { width:1200px; height:50px; background-color:#d9642a; }</style>
            """;

        using var harness = new Harness();
        harness.Cut.SaveProject("wide", new CutProject
        {
            Html = Html,
            Render = new RenderSettings { Width = 1280, Height = 720 },
        });

        var x = Inspector.Examine(harness.Cut, "wide.cut.json");

        Assert.Equal(1280, x.Width);
        Assert.DoesNotContain(x.Findings, f => f.Code == "CF0072");
        Assert.Equal("clean", x.Verdict);
    }

    [Fact]
    public void Two_documents_checked_at_once_do_not_swap_findings()
    {
        // A canary on the ENGINE rather than a test of anything here, kept because of how badly
        // this one bit and how quietly it did it.
        //
        // Through CupriFace 0.25.0 the engine kept ONE diagnostics sink for the whole process, and
        // CupriDoctor.Check drained whatever was in it - so a check returned findings produced by
        // other documents on other threads. 400 interleaved checks of these two: 62 of 200 CLEAN
        // documents reported the other's warning, 110 of 200 that should have warned reported
        // nothing, and none was ever duplicated, which is what said the finding MOVED. Worse, a
        // thread doing nothing but RENDERING polluted 157 of 200 checks of an unrelated document
        // without calling the doctor at all.
        //
        // Nothing here could fix that second case, so this suite ran one class at a time until
        // CupriFace#185 landed in 0.25.1. Re-measured on the upgrade: 0 and 0, and 0 under a
        // concurrent render. This is what says it stays that way, and it is cheap.
        //
        // It first showed up as a shipped composition failing its own lint-clean test for a
        // property it does not contain, and passing whenever it was run alone.
        const string Ignores = """
            <div class="a">x</div>
            <style>body,html{font-family:"Noto Sans";} .a { text-transform: uppercase; width:10px; height:10px; }</style>
            """;
        const string Plain = """
            <div class="b">x</div>
            <style>body,html{font-family:"Noto Sans";} .b { width:10px; height:10px; }</style>
            """;

        var strays = 0;
        var lost = 0;

        Parallel.For(0, 400, i =>
        {
            var ignores = i % 2 == 0;
            var n = Doctor.Check(ignores ? Ignores : Plain, string.Empty, 1280, 720)
                .Count(f => f.Code == "CF0050");

            if (ignores && n == 0) Interlocked.Increment(ref lost);
            if (!ignores && n > 0) Interlocked.Increment(ref strays);
        });

        Assert.Equal(0, strays);
        Assert.Equal(0, lost);
    }

    [Fact]
    public void Declared_events_come_back_with_the_examination()
    {
        const string Html = """
            <div class="scene" data-start="4" data-duration="4" data-track="card"
                 data-cut-event="+1.5:landed">x</div>
            <style>body,html{font-family:"Noto Sans";} .scene { width:40px; height:24px; }</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("e.html", Html));

        var e = Assert.Single(x.Events.Events);
        Assert.Equal("landed", e.Name);
        Assert.Equal(5.5, e.At, 6);
        Assert.Equal("card", e.Track);
        Assert.Equal("clean", x.Verdict);
    }

    [Fact]
    public void An_event_that_cannot_be_read_reaches_the_findings()
    {
        // The alternative is a mark that simply never appears anywhere, and an author who finds
        // out when the thing that was meant to happen does not.
        const string Html = """
            <div class="a" data-cut-event="halfway">x</div>
            <style>body,html{font-family:"Noto Sans";} .a { width:40px; height:24px; }</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("bad.html", Html));

        var found = Assert.Single(x.Findings, f => f.Code == Inspector.EventProblem);
        Assert.Contains("not a time and a name", found.What);
        Assert.Equal("warnings", x.Verdict);
    }

    [Fact]
    public void An_event_nothing_will_ever_reach_is_reported()
    {
        // A timeline that ends at 4s and a mark at 9s. Not an error - a clip is often a window
        // onto something longer - but it is also exactly what a typo looks like.
        const string Html = """
            <div class="a" data-start="0" data-duration="4" data-cut-event="9:never">x</div>
            <style>body,html{font-family:"Noto Sans";} .a { width:40px; height:24px; }</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("late.html", Html));

        var found = Assert.Single(x.Findings, f => f.Code == Inspector.EventProblem);
        Assert.Contains("past the end", found.What);
        Assert.Equal(FindingLevel.Warning, found.Level);
    }

    [Fact]
    public void Declaring_events_does_not_make_a_composition_impure()
    {
        // The whole design in one assertion. An event is a number in a file - it changes no
        // rendering decision, so a composition that declares fifty still renders every frame
        // directly. A callback-based design could not promise this.
        const string Html = """
            <div class="a" data-cut-event="0.5:a; 1:b; 1.5:c">x</div>
            <style>body,html{font-family:"Noto Sans";}
              .a { width:10px; height:24px; animation: grow 2s linear both; }
              @keyframes grow { from { width:0; } to { width:10px; } }
            </style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("pure.html", Html));

        Assert.Equal(3, x.Events.Events.Count);
        Assert.True(x.Purity.PureInTime);
    }

    [Fact]
    public void Every_shipped_composition_lints_clean()
    {
        // The samples are the documentation. One that warns is teaching whatever it warns about.
        using var harness = new Harness();
        var root = Path.Combine(RepoRoot(), "compositions");

        foreach (var file in Directory.EnumerateFiles(root, "*.html").Order())
        {
            var name = Path.GetFileName(file);
            var x = Inspector.Examine(harness.Cut, harness.WriteComposition(name, File.ReadAllText(file)));

            Assert.True(x.Verdict == "clean",
                $"{name}: {x.Verdict}\n" + string.Join("\n", x.Findings
                    .Where(f => f.Level != FindingLevel.Info)
                    .Select(f => $"  {f.Level} {f.Code}: {f.What}")));
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
