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
            <style>body,html{font-family:"Noto Sans";} .a { letter-spacing: 2px; width:10px; height:10px; }</style>
            """;

        using var harness = new Harness();
        var x = Inspector.Examine(harness.Cut, harness.WriteComposition("ls.html", Html));

        Assert.Contains(x.Findings, f => f.Code == "CF0050" && f.What.Contains("letter-spacing"));
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
