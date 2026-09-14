using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The project file's whole reason for existing: a second run, with nothing but a name, can reopen
/// what a first one made, change it, and render it.
/// </summary>
public sealed class ProjectTests
{
    [Fact]
    public void A_project_saved_by_one_service_is_readable_by_another()
    {
        // Two services over one directory stand in for two MCP runs. Nothing is shared but the file.
        using var harness = new Harness();

        var first = harness.Cut;
        first.SaveProject("hero", new CutProject
        {
            Name = "Hero",
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 640, Height = 360, Fps = 24, Duration = 2, Background = "#101014" },
            Meta = new ProjectMeta { Notes = ["the bar should finish growing at 2s"] },
        });

        var second = new Harness2(harness).Cut;
        var reopened = second.LoadProject("hero");

        Assert.Equal("Hero", reopened.Name);
        Assert.Equal(640, reopened.Render.Width);
        Assert.Equal(24, reopened.Render.Fps);
        Assert.Equal("#101014", reopened.Render.Background);
        Assert.Contains("the bar should finish growing at 2s", reopened.Meta.Notes);
        Assert.Contains("@keyframes", reopened.Html);
    }

    [Fact]
    public void A_project_renders_and_supplies_the_arguments_the_caller_left_out()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 320, Height = 180, Scale = 2, Fps = 10, Duration = 1 },
        });

        var frames = new List<(int Width, int Height)>();
        // Only the times are given. Size, scale and rate all come off the project.
        var report = harness.Cut.Sweep(
            new SweepSpec { Composition = "hero.cut.json", Times = [1.0] },
            frame => frames.Add((frame.Image.Width, frame.Image.Height)));

        Assert.Equal((640, 360), frames.Single());
        Assert.Equal(10, report.SweepFps);
        // One frame asked for, one frame rendered: this composition is pure in t, so there is
        // nothing to sweep to. An impure one would render all 11 to reach t=1.
        Assert.True(report.Purity.PureInTime);
        Assert.Equal(1, report.StepsRendered);
        Assert.Equal(0, report.FramesDiscarded);
    }

    [Fact]
    public void An_explicit_argument_beats_the_project()
    {
        // A project is a source of defaults, never an override.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 320, Height = 180, Fps = 10 },
        });

        var size = (0, 0);
        harness.Cut.Sweep(
            new SweepSpec { Composition = "hero.cut.json", Width = 800, Height = 600, Times = [0] },
            frame => size = (frame.Image.Width, frame.Image.Height));

        Assert.Equal((800, 600), size);
    }

    [Fact]
    public void An_inlined_asset_reaches_the_renderer_without_touching_the_disk()
    {
        // A 1x1 red PNG as a data URI. Nothing on disk is named logo.png, so if this renders at all
        // the project carried it.
        const string RedDot =
            "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        using var harness = new Harness();
        var project = new CutProject
        {
            Html = """<cupri-image src="logo.png" style="width:64px;height:64px"></cupri-image>""",
            Css = "body { font-family: \"Noto Sans\"; }",
            Render = new RenderSettings { Width = 100, Height = 100 },
        };
        project.Assets["logo.png"] = RedDot;
        harness.Cut.SaveProject("withasset", project);

        var loaded = harness.Cut.LoadComposition("withasset.cut.json");
        Assert.Contains("data:image/png;base64", loaded.Html);
        Assert.DoesNotContain("\"logo.png\"", loaded.Html);

        var rendered = 0;
        harness.Cut.Sweep(new SweepSpec { Composition = "withasset.cut.json", Times = [0] }, _ => rendered++);
        Assert.Equal(1, rendered);
    }

    [Fact]
    public void Saving_over_a_project_keeps_its_creation_stamp_and_assets()
    {
        using var harness = new Harness();
        var original = new CutProject { Html = "<div></div>", Render = new RenderSettings() };
        original.Assets["logo.png"] = "data:image/png;base64,AAAA";
        harness.Cut.SaveProject("hero", original);
        var created = harness.Cut.LoadProject("hero").Meta.Created;

        // What the update_project tool does: read, change one thing, write back.
        var project = harness.Cut.LoadProject("hero");
        project.Css = ".x { color: red }";
        harness.Cut.SaveProject("hero", project);

        var reopened = harness.Cut.LoadProject("hero");
        Assert.Equal(created, reopened.Meta.Created);
        Assert.Equal(".x { color: red }", reopened.Css);
        Assert.True(reopened.Assets.ContainsKey("logo.png"));
    }

    [Fact]
    public void Only_cut_json_may_be_written_to_the_project_root()
    {
        // The project root is the one read-write directory, so what may land in it is narrow.
        using var harness = new Harness();
        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.ResolveProject("notes.txt", forWriting: true));
        Assert.Contains(".cut.json", ex.Message);
    }

    [Fact]
    public void A_project_path_escaping_the_project_root_is_refused()
    {
        using var harness = new Harness();
        Assert.Throws<CutPolicyException>(() =>
            harness.Cut.ResolveProject(Path.Combine("..", "..", "escape.cut.json"), forWriting: true));
    }

    [Fact]
    public void Listing_finds_what_was_saved()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("a", new CutProject { Html = "<div></div>" });
        harness.Cut.SaveProject("nested/b", new CutProject { Html = "<div></div>" });

        var listed = harness.Cut.ListProjects();
        Assert.Contains("a.cut.json", listed);
        Assert.Contains("nested/b.cut.json", listed);
    }

    /// <summary>A second service over the same directory - a stand-in for a separate MCP run that
    /// shares nothing but the filesystem.</summary>
    private sealed class Harness2(Harness first)
    {
        public CupriCutService Cut { get; } = new(
            new Options(first.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CupriCutService>.Instance)
        { ContentRoot = first.Root };

        private sealed class Options(Configuration.CutOptions value)
            : Microsoft.Extensions.Options.IOptionsMonitor<Configuration.CutOptions>
        {
            public Configuration.CutOptions CurrentValue { get; } = value;
            public Configuration.CutOptions Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<Configuration.CutOptions, string?> listener) => null;
        }
    }
}
