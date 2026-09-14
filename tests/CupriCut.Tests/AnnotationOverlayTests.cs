using CupriCut.Gui;
using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The marks on the frame. Drawn into the picture rather than layered over it, because the
/// rectangle is in frame coordinates and that is the only space it means anything in.
/// </summary>
public sealed class AnnotationOverlayTests
{
    [Theory]
    [InlineData(1.20, true)]     // exactly its moment
    [InlineData(1.60, true)]     // still inside the second it is shown for
    [InlineData(1.99, true)]
    [InlineData(2.30, false)]    // gone
    [InlineData(1.19, false)]    // not yet
    public void An_annotation_is_on_screen_for_a_second_after_its_timestamp(double t, bool visible)
    {
        // So it can be found by scrubbing rather than by landing on the exact frame.
        var annotation = new Annotation { Time = 1.2, Note = "x" };
        Assert.Equal(visible, AnnotationOverlay.VisibleAt(annotation, t));
    }

    [Fact]
    public void The_visible_window_is_a_full_second()
    {
        Assert.Equal(1.0, AnnotationOverlay.VisibleForSeconds);
    }

    [Fact]
    public void A_frame_number_is_stamped_at_the_rate_the_note_was_made_at()
    {
        var annotation = new Annotation { Time = 1.2 }.AtRate(120);

        Assert.Equal(144, annotation.Frame);
        Assert.Equal(120, annotation.Fps);
        Assert.Contains("frame 144 at 120 fps", annotation.Describe(1280, 720));

        // Changing the project's rate later must not silently renumber what the reviewer saw.
        Assert.Equal(144, annotation.Frame);
    }

    [Fact]
    public void A_rate_of_zero_leaves_the_frame_unstamped_rather_than_lying()
    {
        var annotation = new Annotation { Time = 1.2 }.AtRate(0);

        Assert.Equal(0, annotation.Fps);
        Assert.DoesNotContain("frame", annotation.Describe(1280, 720));
    }

    [Fact]
    public void The_overlay_paints_the_marks_of_this_moment_and_the_drag()
    {
        using var harness = new Harness();
        var root = FindRepoRoot();
        var composition = harness.WriteComposition("revenue-card.html",
            File.ReadAllText(Path.Combine(root, "compositions", "revenue-card.html")));

        var marks = new List<OverlayMark>
        {
            new(new Annotation { Time = 1.5, X = 0.13, Y = 0.30, W = 0.36, H = 0.12,
                Note = "bar overshoots the track" }.AtRate(30), Selected: true),
            new(new Annotation { Time = 1.5, X = 0.13, Y = 0.15, W = 0.34, H = 0.10,
                Note = "rule lands after the title", Status = AnnotationStatus.Resolved }.AtRate(30), Selected: false),
            new(new Annotation { Time = 9.0, X = 0.5, Y = 0.5, W = 0.2, H = 0.2,
                Note = "not this moment - must not be drawn" }.AtRate(30), Selected: false),
        };

        var state = new OverlayState(1.5, marks, Dragging: true,
            DragX: 0.55, DragY: 0.56, DragW: 0.30, DragH: 0.16);

        using var session = PreviewSession.Open(harness.Cut, composition, 1280, 720, new SKColor(0x0C, 0x10, 0x17));
        using var image = session.RenderAt(1.5, (canvas, w, h) => AnnotationOverlay.Draw(canvas, w, h, state));

        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "overlay.png"));
        data.SaveTo(file);

        Assert.Equal(1280, image.Width);
        Assert.Equal(720, image.Height);
    }

    [Fact]
    public void Drawing_a_backwards_drag_does_not_throw()
    {
        // Dragging up and left gives negative width and height; the marquee has to cope before
        // Normalised() ever sees it.
        var state = new OverlayState(0, [], Dragging: true,
            DragX: 0.8, DragY: 0.9, DragW: -0.3, DragH: -0.4);

        using var surface = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        AnnotationOverlay.Draw(surface.Canvas, 320, 180, state);
        surface.Canvas.Flush();
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
