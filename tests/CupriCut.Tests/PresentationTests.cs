using CupriCut.Configuration;
using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// Turning a design size into an output frame.
///
/// <para>"Render this at 4K" and "render this at 4K the way it looks at 1080p" are different
/// requests, and the difference is the whole feature. Laying a 1280x720 design out in a 3840-wide
/// viewport reflows it - the card stays 400 CSS pixels wide and is simply lost in a bigger frame.
/// Scaling it keeps the composition and makes it bigger.</para>
/// </summary>
public sealed class PresentationTests
{
    private static readonly CutOptions Defaults = new();

    private static Presentation Resolve(SweepSpec spec, RenderSettings? project = null) =>
        Presentation.Resolve(spec, project, Defaults);

    private static SweepSpec Spec(int w = 0, int h = 0, int scale = 0, int outW = 0, int outH = 0,
        ScalingMode? mode = null) =>
        new()
        {
            Composition = "x.html",
            Times = [0],
            Width = w,
            Height = h,
            Scale = scale,
            OutputWidth = outW,
            OutputHeight = outH,
            Scaling = mode,
        };

    // ---- the compatibility promise --------------------------------------------------------------

    [Theory]
    [InlineData(1280, 720, 1, 1280, 720)]
    [InlineData(1280, 720, 2, 2560, 1440)]
    [InlineData(800, 450, 3, 2400, 1350)]
    public void With_no_output_size_the_pixel_multiplier_is_the_whole_story(
        int w, int h, int scale, int expectedW, int expectedH)
    {
        // What CupriCut did before there were modes at all, and it has to stay exactly this.
        var p = Resolve(Spec(w, h, scale));

        Assert.Equal(expectedW, p.OutputWidth);
        Assert.Equal(expectedH, p.OutputHeight);
        Assert.Equal(w, p.LogicalWidth);
        Assert.Equal(h, p.LogicalHeight);
        Assert.Equal(scale, p.Scale);
        Assert.Equal(0, p.OffsetX);
        Assert.Equal(0, p.OffsetY);
        Assert.True(p.Fills);
    }

    [Fact]
    public void Nothing_named_at_all_is_the_configured_default()
    {
        var p = Resolve(Spec());

        Assert.Equal(Defaults.DefaultWidth, p.OutputWidth);
        Assert.Equal(Defaults.DefaultHeight, p.OutputHeight);
        Assert.Equal(1, p.Scale);
    }

    [Fact]
    public void The_project_supplies_what_the_caller_left_out_and_never_overrides_it()
    {
        var project = new RenderSettings
        {
            Width = 800, Height = 450, OutputWidth = 1600, OutputHeight = 900, Scaling = ScalingMode.Responsive,
        };

        var fromProject = Resolve(Spec(), project);
        Assert.Equal(1600, fromProject.OutputWidth);
        Assert.Equal(ScalingMode.Responsive, fromProject.Mode);

        // ...and the caller still wins.
        var overridden = Resolve(Spec(outW: 640, outH: 360, mode: ScalingMode.Fit), project);
        Assert.Equal(640, overridden.OutputWidth);
        Assert.Equal(ScalingMode.Fit, overridden.Mode);
    }

    // ---- the modes ------------------------------------------------------------------------------

    [Fact]
    public void Fit_scales_the_design_and_keeps_its_shape()
    {
        // The headline case: 4K from a 1080p design, matching aspect, so it fills exactly.
        var p = Resolve(Spec(1920, 1080, outW: 3840, outH: 2160));

        Assert.Equal(1920, p.LogicalWidth);
        Assert.Equal(1080, p.LogicalHeight);
        Assert.Equal(2, p.Scale);
        Assert.True(p.Fills);
    }

    [Fact]
    public void Fit_letterboxes_when_the_aspects_differ()
    {
        // A 16:9 design into a square. The design keeps its shape; the frame gets bars.
        var p = Resolve(Spec(1280, 720, outW: 1080, outH: 1080, mode: ScalingMode.Fit));

        Assert.Equal(1280, p.LogicalWidth);
        Assert.Equal(720, p.LogicalHeight);
        Assert.Equal(1080f / 1280f, p.Scale, 5);      // the tighter axis
        Assert.Equal(0, p.OffsetX);                    // width is the tight one, so no side bars
        Assert.Equal((1080 - 720 * (1080f / 1280f)) / 2, p.OffsetY, 3);
        Assert.False(p.Fills);
    }

    [Fact]
    public void Responsive_lays_out_at_the_output_size_and_reflows()
    {
        var p = Resolve(Spec(1280, 720, outW: 3840, outH: 2160, mode: ScalingMode.Responsive));

        Assert.Equal(3840, p.LogicalWidth);
        Assert.Equal(2160, p.LogicalHeight);
        Assert.Equal(1, p.Scale);
        Assert.True(p.Fills);
    }

    [Fact]
    public void Fixed_is_one_to_one_and_centred()
    {
        var p = Resolve(Spec(1280, 720, outW: 1920, outH: 1080, mode: ScalingMode.Fixed));

        Assert.Equal(1280, p.LogicalWidth);
        Assert.Equal(1, p.Scale);
        Assert.Equal((1920 - 1280) / 2, p.OffsetX);
        Assert.Equal((1080 - 720) / 2, p.OffsetY);
        Assert.False(p.Fills);
    }

    [Fact]
    public void Hybrid_fills_by_reflowing_the_loose_axis_where_Fit_would_letterbox()
    {
        // The same square frame as the Fit test, and the difference is the point: Hybrid spends the
        // extra room on content, Fit spends it on bars.
        var fit = Resolve(Spec(1280, 720, outW: 1080, outH: 1080, mode: ScalingMode.Fit));
        var hybrid = Resolve(Spec(1280, 720, outW: 1080, outH: 1080, mode: ScalingMode.Hybrid));

        Assert.Equal(fit.Scale, hybrid.Scale, 5);      // both zoom the tighter axis
        Assert.Equal(720, fit.LogicalHeight);          // ...but Fit keeps the design's height
        Assert.Equal(1280, hybrid.LogicalHeight);      // ...and Hybrid grows the viewport instead
        Assert.True(hybrid.Fills);
        Assert.False(fit.Fills);
    }

    [Fact]
    public void Adaptive_refuses_to_shrink_below_the_design_size()
    {
        // Hybrid scales down as readily as up; Adaptive floors the scale at 1 and reflows instead.
        var hybrid = Resolve(Spec(1280, 720, outW: 640, outH: 360, mode: ScalingMode.Hybrid));
        var adaptive = Resolve(Spec(1280, 720, outW: 640, outH: 360, mode: ScalingMode.Adaptive));

        Assert.Equal(0.5f, hybrid.Scale, 5);
        Assert.Equal(1, adaptive.Scale);
        Assert.Equal(640, adaptive.LogicalWidth);
    }

    [Fact]
    public void When_the_aspects_match_the_scaling_modes_agree()
    {
        // Worth knowing, because it is the ordinary case: the mode only matters on a mismatch.
        foreach (var mode in new[] { ScalingMode.Fit, ScalingMode.Hybrid, ScalingMode.Adaptive })
        {
            var p = Resolve(Spec(1280, 720, outW: 2560, outH: 1440, mode: mode));
            Assert.Equal(2, p.Scale);
            Assert.Equal(1280, p.LogicalWidth);
            Assert.True(p.Fills);
        }
    }

    // ---- naming one side ------------------------------------------------------------------------

    [Theory]
    [InlineData(3840, 0, 3840, 2160)]
    [InlineData(0, 2160, 3840, 2160)]
    public void Naming_one_side_derives_the_other_from_the_design_aspect(
        int outW, int outH, int expectedW, int expectedH)
    {
        var p = Resolve(Spec(1280, 720, outW: outW, outH: outH));

        Assert.Equal(expectedW, p.OutputWidth);
        Assert.Equal(expectedH, p.OutputHeight);
    }

    // ---- refusals -------------------------------------------------------------------------------

    [Fact]
    public void A_multiplier_and_an_output_size_together_are_refused()
    {
        // Two answers to one question. Quietly picking one is how someone gets a frame they did
        // not ask for - the same reason duration and to cannot both be given.
        var ex = Assert.Throws<ArgumentException>(() =>
            Resolve(Spec(1280, 720, scale: 2, outW: 3840, outH: 2160)));

        Assert.Contains("scale", ex.Message);
        Assert.Contains("3840x2160", ex.Message);
    }

    [Fact]
    public void A_multiplier_of_one_beside_an_output_size_is_fine()
    {
        // scale:1 is the default, not a request, so it cannot be a contradiction.
        var p = Resolve(Spec(1280, 720, scale: 1, outW: 2560, outH: 1440));
        Assert.Equal(2, p.Scale);
    }

    [Theory]
    [InlineData("fit", ScalingMode.Fit)]
    [InlineData("Fit", ScalingMode.Fit)]
    [InlineData("letterbox", ScalingMode.Fit)]
    [InlineData("hybrid", ScalingMode.Hybrid)]
    [InlineData("hybrid-zoom", ScalingMode.Hybrid)]
    [InlineData("hybrid zoom", ScalingMode.Hybrid)]
    [InlineData("responsive", ScalingMode.Responsive)]
    [InlineData("reflow", ScalingMode.Responsive)]
    [InlineData("adaptive", ScalingMode.Adaptive)]
    [InlineData("fixed", ScalingMode.Fixed)]
    [InlineData(null, ScalingMode.Fit)]
    public void The_mode_is_spelled_forgivingly(string? given, ScalingMode expected) =>
        Assert.Equal(expected, Presentation.ParseMode(given));

    [Fact]
    public void An_unknown_mode_names_the_known_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => Presentation.ParseMode("stretch"));

        Assert.Contains("stretch", ex.Message);
        Assert.Contains("hybrid", ex.Message);
    }

    // ---- and it actually renders that way -------------------------------------------------------

    [Fact]
    public void A_scaled_render_is_the_same_picture_bigger_not_a_reflowed_one()
    {
        // The claim the feature rests on, checked in pixels. A 200x100 card centred in a 400x200
        // design: scaled to 800x400 the card must cover the same PROPORTION of the frame. Reflowed,
        // it would stay 200 physical pixels and cover a quarter as much.
        const string Composition = """
            <div class="card"></div>
            <style>
              body, html { margin:0; font-family:"Noto Sans"; }
              .card { position:absolute; left:100px; top:50px; width:200px; height:100px; background:#00ff00; }
            </style>
            """;

        using var harness = new Harness();
        var name = harness.WriteComposition("card.html", Composition);

        var scaled = Green(harness, name, 400, 200, 800, 400, ScalingMode.Fit);
        var reflowed = Green(harness, name, 400, 200, 800, 400, ScalingMode.Responsive);

        // 200x100 of 400x200 is a quarter of the frame, and scaling preserves that.
        Assert.Equal(0.25, scaled, 2);
        // Reflowed, the card keeps its CSS size in a frame with four times the area.
        Assert.Equal(0.0625, reflowed, 2);
    }

    [Fact]
    public void A_letterboxed_render_leaves_the_clear_colour_in_the_bars()
    {
        const string Composition = """
            <div class="fill"></div>
            <style>
              body, html { margin:0; font-family:"Noto Sans"; }
              .fill { position:absolute; left:0; top:0; width:400px; height:200px; background:#00ff00; }
            </style>
            """;

        using var harness = new Harness();
        var name = harness.WriteComposition("fill.html", Composition);

        // A 2:1 design into a square: bars top and bottom, content in the middle.
        SKBitmap? shot = null;
        harness.Cut.Sweep(
            new SweepSpec
            {
                Composition = name,
                Width = 400, Height = 200,
                OutputWidth = 400, OutputHeight = 400,
                Scaling = ScalingMode.Fit,
                Times = [0],
                Background = SKColors.Blue,
            },
            frame => shot = SKBitmap.FromImage(frame.Image));

        using var bitmap = shot!;
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(200, 10));                   // top bar
        Assert.Equal(new SKColor(0x00, 0xFF, 0x00), bitmap.GetPixel(200, 200));  // content
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(200, 390));                  // bottom bar
    }

    /// <summary>The fraction of the frame that is green.</summary>
    private static double Green(Harness harness, string name, int w, int h, int outW, int outH, ScalingMode mode)
    {
        var green = 0L;
        var total = 0L;
        harness.Cut.Sweep(
            new SweepSpec
            {
                Composition = name,
                Width = w, Height = h,
                OutputWidth = outW, OutputHeight = outH,
                Scaling = mode,
                Times = [0],
                Background = SKColors.Black,
            },
            frame =>
            {
                using var bitmap = SKBitmap.FromImage(frame.Image);
                total = (long)bitmap.Width * bitmap.Height;
                for (var y = 0; y < bitmap.Height; y++)
                    for (var x = 0; x < bitmap.Width; x++)
                        if (bitmap.GetPixel(x, y).Green > 200) green++;
            });
        return green / (double)total;
    }
}
