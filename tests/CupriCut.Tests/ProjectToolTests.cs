using System.Text.Json;
using CupriCut.Services;
using CupriCut.Tools;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The tool layer, called the way an MCP client calls it. Separate from ProjectTests because the
/// merge rules - what a re-save keeps, what an update leaves alone - live here rather than in the
/// service, and they are what make a second run's edit safe.
/// </summary>
public sealed class ProjectToolTests
{
    [Fact]
    public void Save_then_update_changes_only_what_was_named()
    {
        using var harness = new Harness();

        ProjectTools.SaveProject(harness.Cut, "hero",
            html: "<div>one</div>", css: ".a{color:red}", title: "Hero",
            width: 800, height: 450, fps: 25, duration: 2, notes: ["first pass"]);

        ProjectTools.UpdateProject(harness.Cut, "hero", css: ".a{color:blue}", notes: ["second pass"]);

        var project = harness.Cut.LoadProject("hero");
        Assert.Equal("<div>one</div>", project.Html);       // untouched
        Assert.Equal(".a{color:blue}", project.Css);        // replaced
        Assert.Equal(800, project.Render.Width);            // untouched
        Assert.Equal(2, project.Render.Duration);
        Assert.Equal(["first pass", "second pass"], project.Meta.Notes);  // appended, not replaced
    }

    [Fact]
    public void Update_with_nothing_named_says_so_rather_than_pretending()
    {
        using var harness = new Harness();
        ProjectTools.SaveProject(harness.Cut, "hero", html: "<div></div>");

        var result = JsonSerializer.Deserialize<JsonElement>(ProjectTools.UpdateProject(harness.Cut, "hero"));
        var changed = result.GetProperty("changed").EnumerateArray().Select(x => x.GetString()).ToList();

        Assert.Single(changed);
        Assert.Contains("nothing", changed[0]);
    }

    [Fact]
    public void Attaching_an_asset_inlines_it_and_it_survives_a_later_update()
    {
        using var harness = new Harness();
        File.WriteAllBytes(Path.Combine(harness.Compositions, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x0D]);

        ProjectTools.SaveProject(harness.Cut, "hero", html: """<cupri-image src="logo.png"></cupri-image>""");
        var attached = JsonSerializer.Deserialize<JsonElement>(
            ProjectTools.AttachAsset(harness.Cut, "hero", "logo.png"));

        Assert.Equal("logo.png", attached.GetProperty("asset").GetString());

        var project = harness.Cut.LoadProject("hero");
        Assert.StartsWith("data:image/png;base64,", project.Assets["logo.png"]);

        // The asset must not be a casualty of editing the CSS afterwards.
        ProjectTools.UpdateProject(harness.Cut, "hero", css: ".x{}");
        Assert.True(harness.Cut.LoadProject("hero").Assets.ContainsKey("logo.png"));
    }

    [Fact]
    public void An_asset_from_outside_the_composition_roots_is_refused()
    {
        using var harness = new Harness();
        ProjectTools.SaveProject(harness.Cut, "hero", html: "<div></div>");

        Assert.Throws<CutPolicyException>(() =>
            ProjectTools.AttachAsset(harness.Cut, "hero", Path.Combine("..", "..", "secret.png")));
    }

    [Fact]
    public void Listing_reports_what_a_second_run_needs_to_choose_between_projects()
    {
        using var harness = new Harness();
        ProjectTools.SaveProject(harness.Cut, "a", html: "<div></div>", title: "The A one",
            description: "first", width: 640, height: 360, fps: 24, duration: 5);

        var listed = JsonSerializer.Deserialize<JsonElement>(ProjectTools.ListProjects(harness.Cut));
        var first = listed.GetProperty("projects").EnumerateArray().Single();

        Assert.Equal("a.cut.json", first.GetProperty("file").GetString());
        Assert.Equal("The A one", first.GetProperty("name").GetString());
        Assert.Equal("640x360", first.GetProperty("size").GetString());
        Assert.Equal(5, first.GetProperty("duration").GetDouble());
    }

    [Fact]
    public void Load_summarises_assets_by_default_so_a_data_uri_does_not_flood_the_answer()
    {
        using var harness = new Harness();
        File.WriteAllBytes(Path.Combine(harness.Compositions, "logo.png"), new byte[4096]);
        ProjectTools.SaveProject(harness.Cut, "hero", html: "<div></div>");
        ProjectTools.AttachAsset(harness.Cut, "hero", "logo.png");

        var summarised = ProjectTools.LoadProject(harness.Cut, "hero");
        Assert.DoesNotContain("data:image/png;base64", summarised);

        var full = ProjectTools.LoadProject(harness.Cut, "hero", summariseAssets: false);
        Assert.Contains("data:image/png;base64", full);
    }
}
