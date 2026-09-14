using CupriCut.Gui;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The producer half of the engine's live-surface contract:
/// "never dispose an image the paint path may still be reading - swap first, dispose the PREVIOUS
/// frame after the next repaint (or keep a small pool)."
///
/// <para>Publishing used to dispose the outgoing frame on the spot. That survived while a preview
/// was one frame per scrub, seconds apart, and became a crash the moment playback published sixty a
/// second against a painter reading them.</para>
/// </summary>
public sealed class PreviewSurfaceTests
{
    [Fact]
    public void A_published_frame_survives_a_reader_holding_it_across_the_next_publish()
    {
        using var surface = new PreviewSurface();
        surface.Publish(Frame(1));

        // What the paint path does: resolve the frame, then draw it. Another publish lands in
        // between - which is exactly the window the old code disposed in.
        var held = surface.CurrentFrame;
        Assert.NotNull(held);

        surface.Publish(Frame(2));

        // The held image must still be usable. On a disposed SKImage this throws or crashes.
        using var target = SKSurface.Create(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        target.Canvas.DrawImage(held, 0, 0);
        target.Canvas.Flush();

        Assert.Equal(8, held!.Width);
    }

    [Fact]
    public void Publishing_hard_while_painting_does_not_crash_or_leak_unboundedly()
    {
        // The playback case: a producer at full speed against a reader at full speed.
        using var surface = new PreviewSurface();
        using var target = SKSurface.Create(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul))!;

        var stop = false;
        var painter = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                if (surface.CurrentFrame is { } frame)
                {
                    target.Canvas.DrawImage(frame, 0, 0);
                    target.Canvas.Flush();
                }
            }
        });

        for (var i = 0; i < 600; i++) surface.Publish(Frame(i % 8 + 1));

        Volatile.Write(ref stop, true);
        painter.GetAwaiter().GetResult();          // rethrows anything the painter hit

        // A pool, not a leak: retired frames are released as publishing moves past them.
        Assert.True(surface.RetainedFrames <= 8, $"{surface.RetainedFrames} frames retained");
    }

    private static SKImage Frame(int seed)
    {
        var info = new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var s = SKSurface.Create(info)!;
        s.Canvas.Clear(new SKColor((byte)(seed * 20), 0, 0));
        s.Canvas.Flush();
        return s.Snapshot();
    }
}
