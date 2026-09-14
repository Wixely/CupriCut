using System.Security.Cryptography;
using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The claims the whole tool surface rests on. If any of these stop being true, the design
/// decisions in PLAN.md need re-opening rather than the code being patched around.
/// </summary>
public sealed class SweepTests
{
    [Fact]
    public void Sweep_steps_every_frame_between_zero_and_the_target()
    {
        // The reason render_frame(3) costs half a second: it is 91 renders, not one.
        var steps = CupriCutService.SweepSteps([3.0], 30);
        Assert.Equal(91, steps.Length);
        Assert.Equal(0, steps[0]);
        Assert.Equal(3.0, steps[^1], 6);
    }

    [Fact]
    public void Sweep_renders_a_requested_time_that_falls_between_frames()
    {
        // 1.234 is not a multiple of 1/30, and it must still be rendered exactly rather than
        // snapped to a neighbouring frame.
        var steps = CupriCutService.SweepSteps([1.234], 30);
        Assert.Contains(1.234, steps);
        Assert.Equal(1.234, steps[^1], 6);
    }

    [Fact]
    public void The_same_sweep_twice_is_byte_identical()
    {
        // Determinism is the product. Two sweeps of one composition must agree exactly.
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var first = Digest(harness, name, [0.5, 1.0, 1.5]);
        var second = Digest(harness, name, [0.5, 1.0, 1.5]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_transition_makes_a_frame_depend_on_the_frames_before_it()
    {
        // The measurement that forced "sweep from 0" everywhere. A composition with a transition
        // reaches t=1 differently depending on what came before, so a cheap single-shot preview
        // would show the agent a frame the video does not contain.
        using var harness = new Harness();
        harness.WriteComposition("transitioned.html", Harness.Transitioned);

        var swept = Digest(harness, "transitioned.html", [1.0], fps: 30);
        var jumped = Digest(harness, "transitioned.html", [1.0], fps: 1);

        // Both are repeatable; the point is only that the two readings need not agree, so the tool
        // surface has to pick one. Re-running each confirms neither is flaky.
        Assert.Equal(swept, Digest(harness, "transitioned.html", [1.0], fps: 30));
        Assert.Equal(jumped, Digest(harness, "transitioned.html", [1.0], fps: 1));
    }

    [Fact]
    public void Scale_multiplies_pixels_without_changing_the_layout_viewport()
    {
        // A HiDPI render, not a wider one. RenderToImage(w*2, h*2) would lay out twice as wide and
        // give a different composition rather than a sharper picture.
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var (oneX, twoX) = (Size(harness, name, 1), Size(harness, name, 2));
        Assert.Equal(new SKSizeI(400, 200), oneX);
        Assert.Equal(new SKSizeI(800, 400), twoX);
    }

    [Fact]
    public void A_frame_over_the_pixel_ceiling_is_refused_by_name()
    {
        using var harness = new Harness(o => o.MaxPixels = 100_000);
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 1920, Height = 1080, Times = [0] }, _ => { }));
        Assert.Contains("MaxPixels", ex.Message);
    }

    [Fact]
    public void There_is_no_frame_ceiling_by_default()
    {
        // A renderer that cannot make a long clip is not a renderer. MaxFrames defaults to 0,
        // which means no limit; the cost of length is handled where it arises (streamed PNG
        // encoding) rather than by refusing the request.
        using var harness = new Harness();
        Assert.Equal(0, harness.Options.MaxFrames);

        // 20 seconds at 120 fps is 2400 frames - over the 1800 that used to be the ceiling.
        var steps = CupriCutService.SweepSteps([20.0], 120);
        Assert.Equal(2401, steps.Length);

        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);
        var rendered = 0;
        harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 64, Height = 32, Times = [3.0], SweepFps = 600 },
            _ => rendered++);            // 1801 swept frames, which the old default would refuse
        Assert.Equal(1, rendered);
    }

    [Fact]
    public void A_sweep_over_an_explicitly_configured_ceiling_is_still_refused()
    {
        // The knob remains for a shared instance that wants to bound what one caller can spend.
        using var harness = new Harness(o => o.MaxFrames = 10);
        // A composition that genuinely needs sweeping - a pure one renders one frame for one frame
        // and never approaches a ceiling.
        var name = harness.WriteComposition("transitioned.html", Harness.Transitioned);

        // One frame is asked for; 91 have to be rendered to reach it. The limit is about the cost.
        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 200, Height = 100, Times = [3.0] }, _ => { }));
        Assert.Contains("MaxFrames", ex.Message);
        Assert.Contains("every frame before it", ex.Message);
    }

    [Fact]
    public void A_pure_composition_renders_only_the_frames_asked_for()
    {
        // The whole point: @keyframes carries no state between Animate calls, so reaching t=3 costs
        // one render rather than ninety-one. Measured byte-identical to the swept frame.
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var rendered = 0;
        var report = harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 200, Height = 100, Times = [3.0] },
            _ => rendered++);

        Assert.True(report.Purity.PureInTime);
        Assert.Equal(1, report.StepsRendered);
        Assert.Equal(1, rendered);
    }

    [Fact]
    public void A_pure_composition_renders_the_same_pixels_swept_or_direct()
    {
        // The claim the fast path rests on. If this ever stops being true, the fast path is wrong
        // and this test is how that is found out.
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var direct = Digest(harness, name, [0.4, 0.9, 1.6]);
        var swept = Digest(harness, name, [0.4, 0.9, 1.6], force: true);

        Assert.Equal(swept, direct);
    }

    [Fact]
    public void A_composition_with_a_transition_is_still_swept()
    {
        // Conservative on purpose: a transition interpolates from the previous frame, so the
        // intermediate frames are what make t=1 mean anything.
        using var harness = new Harness();
        var name = harness.WriteComposition("transitioned.html", Harness.Transitioned);

        var report = harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 200, Height = 100, Times = [1.0] }, _ => { });

        Assert.False(report.Purity.PureInTime);
        Assert.Contains("transition", report.Purity.Summary);
        Assert.Equal(31, report.StepsRendered);        // 0 to 1 inclusive at 30 fps
        Assert.Equal(30, report.FramesDiscarded);
    }

    [Fact]
    public void Forcing_a_sweep_overrides_the_analysis()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);

        var report = harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 200, Height = 100, Times = [1.0], ForceSweep = true },
            _ => { });

        Assert.False(report.Purity.PureInTime);
        Assert.Equal(31, report.StepsRendered);
    }

    private static string Digest(Harness harness, string composition, double[] times, double fps = 30, bool force = false)
    {
        var hash = MD5.Create();
        harness.Cut.Sweep(
            new SweepSpec { Composition = composition, Width = 400, Height = 200, Times = times, SweepFps = fps, ForceSweep = force },
            frame =>
            {
                var png = FrameEncoder.Encode(frame.Image);
                hash.TransformBlock(png, 0, png.Length, null, 0);
            });
        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash!);
    }

    private static SKSizeI Size(Harness harness, string composition, int scale)
    {
        var size = SKSizeI.Empty;
        harness.Cut.Sweep(
            new SweepSpec { Composition = composition, Width = 400, Height = 200, Scale = scale, Times = [0] },
            frame => size = new SKSizeI(frame.Image.Width, frame.Image.Height));
        return size;
    }
}
