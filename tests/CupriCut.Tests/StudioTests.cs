using CupriCut.Gui;
using CupriCut.Services;
using CupriFace;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The studio window, without a window.
///
/// <para>The GUI is a <c>CupriApp</c>, so its markup, style, binding and layout can be rendered
/// headlessly with the same <c>RenderToImage</c> the product uses on compositions — which is what
/// makes any of this testable in CI. What is NOT covered here is the windowing itself: whether a
/// GL surface comes up, whether a real mouse produces the pointer phases. That needs a display.</para>
/// </summary>
public sealed class StudioTests
{
    [Fact]
    public void The_window_lays_out_and_renders_without_a_display()
    {
        using var harness = new Harness();
        var model = new StudioModel();
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };

        using var doc = Open(app);
        using var image = doc.RenderToImage(app.Width, app.Height);

        Assert.Equal(app.Width, image.Width);
        Assert.Equal(app.Height, image.Height);
    }

    [Fact]
    public void The_preview_element_is_findable_by_its_surface_key()
    {
        // The drag maths needs the preview's box, and it is located by the surface key rather than
        // by class name. If the markup ever stops declaring it, annotation silently breaks.
        using var harness = new Harness();
        var model = new StudioModel();
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };

        using var doc = Open(app);
        doc.Surfaces.Register(StudioApp.PreviewKey, new PreviewSurface());
        using (doc.RenderToImage(app.Width, app.Height)) { }

        var node = FindSurface(doc.Root, StudioApp.PreviewKey);
        Assert.NotNull(node);
        Assert.True(node!.Width > 100, $"the preview box is {node.Width}px wide");
        Assert.True(node.Height > 100, $"the preview box is {node.Height}px tall");
    }

    [Fact]
    public void The_project_list_and_annotation_list_reach_the_markup()
    {
        using var harness = new Harness();
        var model = new StudioModel
        {
            Projects = [new ProjectRow { File = "hero.cut.json", Name = "Hero card", Detail = "1280x720" }],
            Annotations = [new AnnotationRow { Id = "abc", Note = "logo enters too late", At = "t = 1.2s", Region = "200x80 at (40,600)" }],
            SelectedTitle = "Hero card",
        };
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };

        using var doc = Open(app);
        using (doc.RenderToImage(app.Width, app.Height)) { }

        var text = AllText(doc.Root);
        Assert.Contains("Hero card", text);
        Assert.Contains("logo enters too late", text);
        Assert.Contains("t = 1.2s", text);
    }

    // ---- the drag maths, which is what an annotation's accuracy rests on ---------------------

    [Theory]
    // box at (100,50) 800x400: a click at its top-left is 0,0 and bottom-right is 1,1
    [InlineData(100, 50, 0, 0)]
    [InlineData(900, 450, 1, 1)]
    [InlineData(500, 250, 0.5, 0.5)]
    public void A_point_on_the_preview_normalises_to_the_frame(float x, float y, double nx, double ny)
    {
        var box = SKRect.Create(100, 50, 800, 400);
        var (gx, gy) = StudioController.Normalise(box, x, y);

        Assert.Equal(nx, gx, 6);
        Assert.Equal(ny, gy, 6);
    }

    [Fact]
    public void A_point_outside_the_preview_is_clamped_rather_than_escaping_the_frame()
    {
        // A drag that runs off the edge must still describe a region inside the picture.
        var box = SKRect.Create(100, 50, 800, 400);

        Assert.Equal((0d, 0d), StudioController.Normalise(box, -500, -500));
        Assert.Equal((1d, 1d), StudioController.Normalise(box, 5000, 5000));
    }

    [Fact]
    public void Normalising_a_zero_sized_box_does_not_divide_by_zero()
    {
        // Before the first layout the node has no size, and a pointer can still arrive.
        Assert.Equal((0d, 0d), StudioController.Normalise(SKRect.Create(0, 0, 0, 0), 10, 10));
    }

    // ---- annotations -------------------------------------------------------------------------

    [Fact]
    public void A_backwards_drag_still_yields_a_sane_region()
    {
        // Dragging up and left gives negative width and height; the stored rectangle must not.
        var annotation = new Annotation { X = 0.8, Y = 0.9, W = -0.3, H = -0.4 }.Normalised();

        Assert.Equal(0.5, annotation.X, 6);
        Assert.Equal(0.5, annotation.Y, 6);
        Assert.Equal(0.3, annotation.W, 6);
        Assert.Equal(0.4, annotation.H, 6);
    }

    [Fact]
    public void A_region_survives_the_project_being_rendered_at_another_size()
    {
        // The reason coordinates are normalised: a note drawn on a 1280x720 preview has to mean the
        // same part of the picture at 3840x2160.
        var annotation = new Annotation { X = 0.25, Y = 0.5, W = 0.25, H = 0.25 };

        Assert.Equal((320, 360, 320, 180), annotation.InPixels(1280, 720));
        Assert.Equal((960, 1080, 960, 540), annotation.InPixels(3840, 2160));
    }

    [Fact]
    public void An_annotation_round_trips_through_the_project_file()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 1280, Height = 720 },
        });

        harness.Cut.EditProject("hero", p => p.Annotations.Add(new Annotation
        {
            Time = 1.25,
            X = 0.1,
            Y = 0.2,
            W = 0.3,
            H = 0.4,
            Note = "logo enters too late",
        }));

        var reopened = harness.Cut.LoadProject("hero");
        var annotation = Assert.Single(reopened.Annotations);

        Assert.Equal(1.25, annotation.Time);
        Assert.Equal("logo enters too late", annotation.Note);
        Assert.Equal(AnnotationStatus.Open, annotation.Status);
        Assert.Single(reopened.OpenAnnotations);
        Assert.Contains("t=1.25s", annotation.Describe(1280, 720));
    }

    [Fact]
    public void The_status_enum_round_trips_as_a_name_rather_than_a_number()
    {
        // A project file a human may read, and "resolved" beats "1".
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = "<div></div>",
            Annotations = [new Annotation { Note = "x", Status = AnnotationStatus.Resolved }],
        });

        var json = File.ReadAllText(harness.Cut.ResolveProject("hero", forWriting: false));
        Assert.Contains("\"Resolved\"", json);
        Assert.Equal(AnnotationStatus.Resolved, harness.Cut.LoadProject("hero").Annotations[0].Status);
    }

    [Fact]
    public void A_populated_window_hides_its_empty_states_and_paints_the_preview()
    {
        // Also writes studio.png beside the test binary, which is how the window gets reviewed at
        // all: it has no display in CI, and the layout is worth looking at when it changes.
        var model = new StudioModel
        {
            Projects =
            [
                new ProjectRow { File = "hero.cut.json", Name = "Hero card", Detail = "1280x720 · 30fps · 3s", IsSelected = true, Badge = "2 open annotations" },
                new ProjectRow { File = "timebase.cut.json", Name = "Timebase", Detail = "1280x720 · 120fps · 10s" },
                new ProjectRow { File = "revenue.cut.json", Name = "Revenue by region", Detail = "1280x720 · 30fps · 2.6s" },
            ],
            Selected = "hero.cut.json",
            SelectedTitle = "Hero card",
            Time = 1.2, Duration = 3, FrameWidth = 1280, FrameHeight = 720,
            HasFrame = true, Marking = true,
            PendingNote = "logo should land before the subtitle",
            Status = "t = 1.2s · swept 37 frames in 310ms",
            Annotations =
            [
                new AnnotationRow { Id = "a1", Note = "logo enters too late", At = "t = 1.2s", Region = "320x180 at (40,600)" },
                new AnnotationRow { Id = "a2", Note = "subtitle is too dim against the gradient", At = "t = 2.0s", Region = "540x60 at (120,540)" },
                new AnnotationRow { Id = "a3", Note = "rule draws past the card edge", At = "t = 0.8s", Region = "200x12 at (900,300)", Resolved = true, StatusLabel = "resolved" },
            ],
        };
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };
        using var doc = Open(app);

        // A real rendered frame on the real surface, so the shot proves the preview path rather
        // than just the chrome around it.
        using var harness = new Harness();
        var composition = harness.WriteComposition("shot.html", ShotComposition);
        using var surface = new PreviewSurface();
        harness.Cut.Sweep(
            new SweepSpec { Composition = composition, Width = 858, Height = 482, Times = [1.2] },
            frame =>
            {
                using var bitmap = FrameEncoder.Copy(frame.Image);
                surface.Publish(SKImage.FromBitmap(bitmap));
            });
        doc.Surfaces.Register(StudioApp.PreviewKey, surface);

        // Both sizes: the design size, and the halved logical box a 200% display gives - because a
        // layout of hardcoded columns looks perfect at one and clips its right-hand panel at the
        // other, which is exactly what happened the first time this window opened for real.
        foreach (var (w, h, name) in new[] { (app.Width, app.Height, "studio.png"), (960, 600, "studio-small.png") })
        {
            using var img = doc.RenderToImage(w, h, new SKColor(0x0F, 0x13, 0x1A));
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(Path.Combine(AppContext.BaseDirectory, name));
            data.SaveTo(fs);
        }

        using var img2 = doc.RenderToImage(app.Width, app.Height, new SKColor(0x0F, 0x13, 0x1A));

        // The empty-state copy must be gone when the lists are full - the bug this shot caught the
        // first time, because the markup used a conditional attribute the engine does not have.
        var text = AllText(doc.Root);
        Assert.DoesNotContain("No projects yet", text);
        Assert.DoesNotContain("Nothing marked", text);
        Assert.DoesNotContain("Select a project to preview", text);
        Assert.DoesNotContain("rendering", text);

        // ...and the state that IS on must be showing.
        Assert.Contains("Marking: drag on the frame", text);
        Assert.Contains("Cancel", text);

        // The surface really reached the stage: its node carries the key and has a real box.
        var stage = FindSurface(doc.Root, StudioApp.PreviewKey);
        Assert.NotNull(stage);
        Assert.True(stage!.Width > 500, $"the preview is {stage.Width}px wide");

        // Nothing may overflow the viewport: the annotations rail sitting off the right edge is
        // invisible in a headless render unless it is asserted.
        var widest = Rightmost(doc.Root);
        Assert.True(widest <= app.Width + 1, $"content reaches {widest}px in a {app.Width}px window");
        Assert.NotNull(surface.CurrentFrame);
        Assert.Equal((858, 482), surface.NaturalSize);
    }

    private const string ShotComposition = """
        <div class="card"><div class="logo">ACME</div><div class="sub">Quarterly review</div></div>
        <style>
          .card { width:100%; height:100%; font-family:"Noto Sans";
                  background:linear-gradient(150deg,#12161f 0%,#1d2736 100%); }
          .logo { width:600px; margin:150px 0 0 90px; font-size:64px; font-weight:700; color:#f4f6fb;
                  animation:rise 1s ease-out both; }
          .sub  { width:600px; margin:14px 0 0 92px; font-size:24px; color:#d9642a;
                  animation:rise 1s ease-out 0.3s both; }
          @keyframes rise { from { opacity:0; transform:translateY(30px); } to { opacity:1; transform:translateY(0); } }
        </style>
        """;

    // ---- helpers ------------------------------------------------------------------------------

    private static CupriDocument Open(StudioApp app)
    {
        var doc = CupriDocument.Load(app.Html, app.Css).UseComponents(app.Components);
        foreach (var font in app.Fonts) doc.LoadFont(font);
        doc.Bind(app.Model!);
        return doc;
    }

    private static IEnumerable<CupriFace.Resources.CupriSource> FontFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fonts"), "*.ttf")
            .Order(StringComparer.Ordinal)
            .Select(CupriFace.Resources.CupriSource.File);

    private static CupriFace.Dom.RenderNode? FindSurface(CupriFace.Dom.RenderNode node, string key)
    {
        if (node.SurfaceKey == key) return node;
        foreach (var child in node.Children)
            if (FindSurface(child, key) is { } hit) return hit;
        return null;
    }

    /// <summary>The furthest right edge in the tree - how a clipped panel is caught without eyes.</summary>
    private static float Rightmost(CupriFace.Dom.RenderNode node)
    {
        var edge = node.X + node.Width;
        foreach (var child in node.Children) edge = Math.Max(edge, Rightmost(child));
        return edge;
    }

    private static string AllText(CupriFace.Dom.RenderNode node)
    {
        var sb = new System.Text.StringBuilder();
        Walk(node);
        return sb.ToString();

        void Walk(CupriFace.Dom.RenderNode n)
        {
            if (n.Text is { Length: > 0 }) sb.Append(n.Text).Append(' ');
            foreach (var child in n.Children) Walk(child);
        }
    }
}
