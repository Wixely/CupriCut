using CupriCut.Gui;
using CupriFace.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// The engine's own check on the window's markup.
///
/// <para><b>Why this is here.</b> The format dropdown was written without an <c>open</c> binding.
/// It rendered perfectly, took the click, reported it handled - and never opened, because a
/// <c>cupri-select</c> keeps its open state in the MODEL. Nothing threw and nothing logged; it just
/// behaved like an expensive label. The engine has a diagnostic for precisely that (CF0021) and it
/// costs one test to be told, rather than finding out by driving a real window with a real mouse
/// and squinting at screenshots.</para>
///
/// <para>It reads the engine rather than describing it: unrendered elements come from diffing the
/// real render tree, unknown components from the real registry, unsupported CSS from the real
/// parser. So it stays true as the engine moves.</para>
/// </summary>
public sealed class DoctorTests(ITestOutputHelper output)
{
    [Fact]
    public void The_studio_markup_has_no_errors()
    {
        var model = Populated();
        var app = new StudioApp(model);

        var report = CupriDoctor.Check(app.Html, app.Css, app.Components, model: app.Model);
        output.WriteLine(report.ToString());

        Assert.False(report.HasErrors, report.ToString());
    }

    [Fact]
    public void Every_control_that_opens_has_somewhere_to_keep_that()
    {
        // CF0021 on its own, named, so a regression reads as what it is rather than as "the markup
        // got worse". A control that can never open is invisible in a screenshot.
        var app = new StudioApp(Populated());
        var report = CupriDoctor.Check(app.Html, app.Css, app.Components, model: app.Model);

        var inert = report.Findings.Where(f => f.Code == "CF0021").ToList();
        Assert.True(inert.Count == 0,
            "a control was given no open binding, so it can never open:\n" +
            string.Join("\n", inert.Select(f => $"  {f.Code} (line {f.Line}): {f.Message}")));
    }

    /// <summary>A model with something in every collection. An empty one hides the bindings inside
    /// <c>data-repeat</c>, which is where the interesting mistakes are.</summary>
    private static StudioModel Populated() => new()
    {
        Selected = "hero.cut.json",
        HasBackdrop = true,
        Projects =
        [
            new ProjectRow { File = "hero.cut.json", Name = "Hero card", Detail = "1280x720", IsSelected = true, Badge = "2 open" },
        ],
        Annotations =
        [
            new AnnotationRow { Id = "a1", Note = "logo enters too late", At = "t = 1.2s", FrameAt = "frame 36 at 30 fps", Region = "320x180 at (40,600)" },
        ],
        Calibration =
        [
            new CalibrationRow { Group = "Tools", Name = "ffmpeg", Ok = true, Detail = "ffmpeg version N-91454" },
        ],
    };
}
