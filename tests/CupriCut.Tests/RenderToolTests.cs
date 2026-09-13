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
        // A project's duration is a LENGTH, so 0.5s at 10 fps is 5 files covering [0, 0.5) - the
        // last at 0.4s. An inclusive reading would give 6 files and half a second plus one frame.
        Assert.Equal(5, json.GetProperty("files").GetArrayLength());
        Assert.Equal(0.4, json.GetProperty("to").GetDouble(), 6);
        Assert.Equal(0.5, json.GetProperty("seconds").GetDouble(), 6);
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

    [Theory]
    [InlineData(10, 120, 1200)]   // the case that found this: 10s at 120fps
    [InlineData(10, 30, 300)]
    [InlineData(2, 30, 60)]
    [InlineData(0.5, 120, 60)]
    public void A_clip_of_n_seconds_is_exactly_n_times_fps_frames(double duration, double fps, int expected)
    {
        // The off-by-one this exists to prevent: an inclusive range would give n*fps+1 frames and a
        // file that overruns by one frame - 10.008s instead of 10.000s at 120fps.
        var times = ToolSupport.Clip(0, duration, fps);

        Assert.Equal(expected, times.Length);
        Assert.Equal(0, times[0]);
        Assert.Equal(duration - 1 / fps, times[^1], 6);      // ends one frame short of the duration
        Assert.Equal(duration, times.Length / fps, 6);       // ...which is what makes the length exact
    }

    [Fact]
    public void An_inclusive_range_is_one_frame_longer_than_the_same_number_as_a_duration()
    {
        // Both are correct answers to different questions, and the difference is the bug when the
        // wrong one is used for a clip length.
        Assert.Equal(1200, ToolSupport.Clip(0, 10, 120).Length);
        Assert.Equal(1201, ToolSupport.Range(0, 10, 120).Length);
    }

    [Fact]
    public void Clip_rounds_rather_than_truncates_a_binary_inexact_product()
    {
        // 10 * 120 is 1199.9999999999998 in double. Truncating loses a frame and makes the clip
        // short, which is the same class of bug in the other direction.
        Assert.Equal(1199.9999999999998, 10 * 120d, 10);
        Assert.Equal(1200, ToolSupport.Clip(0, 10, 120).Length);
    }

    [Fact]
    public void A_projects_duration_is_a_length_so_its_video_is_exactly_that_long()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 80, Height = 40, Fps = 20, Duration = 1.5 },
        });

        var json = JsonSerializer.Deserialize<JsonElement>(
            RenderTools.RenderFrames(harness.Cut, "hero.cut.json"));

        Assert.Equal(30, json.GetProperty("files").GetArrayLength());   // 1.5s x 20fps, not 31
        Assert.Equal(1.5, json.GetProperty("seconds").GetDouble(), 4);
    }

    [Fact]
    public void Duration_and_to_together_are_refused_rather_than_silently_picking_one()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var ex = Assert.Throws<ArgumentException>(() =>
            RenderTools.RenderVideo(harness.Cut, harness.Encoder, name, duration: 2, to: 2));
        Assert.Contains("one frame", ex.Message);
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
