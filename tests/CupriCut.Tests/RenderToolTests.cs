using System.Text.Json;
using CupriCut.Services;
using CupriCut.Tools;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The render tools as an MCP client meets them: what comes back, and what a project fills in when
/// the caller says nothing.
/// </summary>
public sealed class RenderToolTests
{
    [Fact]
    public void Render_frame_returns_an_image_block_and_a_line_about_what_it_cost()
    {
        // The first tool returns a picture. That is the whole premise, so it is worth a test.
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var result = RenderTools.RenderFrame(harness.Cut, name, t: 0.5, width: 200, height: 100);

        var image = Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Data.Length > 0);

        var text = Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>());
        Assert.Contains("framesSwept", text.Text);
    }

    [Fact]
    public void A_picture_too_big_for_a_context_is_written_out_instead_of_returned()
    {
        // Filling an agent's context with megabytes of base64 helps nobody, and silently truncating
        // would be worse. It says what it did and where the file is.
        using var harness = new Harness(o => o.MaxInlineImageBytes = 128);
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var result = RenderTools.RenderFrame(harness.Cut, name, t: 0.5, width: 400, height: 200);

        Assert.Empty(result.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>());
        var text = Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>());
        Assert.Contains("MaxInlineImageBytes", text.Text);
    }

    [Fact]
    public void Render_frames_takes_its_range_from_the_project_when_none_is_given()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 160, Height = 90, Fps = 10, Duration = 0.5 },
        });

        var json = JsonSerializer.Deserialize<JsonElement>(
            RenderTools.RenderFrames(harness.Cut, "hero.cut.json"));

        Assert.Equal(10, json.GetProperty("fps").GetDouble());
        Assert.Equal(0.5, json.GetProperty("to").GetDouble());
        Assert.Equal(6, json.GetProperty("files").GetArrayLength());   // 0 to 0.5 inclusive at 10 fps
    }

    [Fact]
    public void A_contact_sheet_of_a_project_needs_no_duration_argument()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 160, Height = 90, Fps = 10, Duration = 1.0 },
        });

        var result = RenderTools.ContactSheet(harness.Cut, "hero.cut.json", count: 4, thumbnailWidth: 80);
        var text = Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>());

        Assert.Contains("t = 0s to t = 1s", text.Text);
        Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>());
    }

    [Fact]
    public void A_projects_scale_applies_when_the_caller_names_none()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 100, Height = 50, Scale = 3 },
        });

        var json = JsonSerializer.Deserialize<JsonElement>(
            RenderTools.RenderFrames(harness.Cut, "hero.cut.json", to: 0.1));

        Assert.Equal("300x150", json.GetProperty("cost").GetProperty("size").GetString());
    }

    [Fact]
    public void A_bad_colour_is_refused_by_name_rather_than_rendered_black()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var ex = Assert.Throws<ArgumentException>(() =>
            RenderTools.RenderFrame(harness.Cut, name, width: 100, height: 100, background: "chartreusey"));
        Assert.Contains("chartreusey", ex.Message);
    }
}
