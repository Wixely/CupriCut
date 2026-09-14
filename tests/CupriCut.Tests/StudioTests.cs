using CupriCut.Gui;
using CupriCut.Services;
using CupriFace;
using CupriFace.Interaction;
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

    // ---- the drag, driven through the same entry point the window uses -----------------------

    [Fact]
    public void Dragging_a_box_on_the_preview_writes_an_annotation_to_the_project()
    {
        // DispatchPointer is what a desktop host calls for a real mouse, so this exercises the whole
        // path - hit testing, the capture OnMark takes on Down, the coordinate normalisation and the
        // write - without needing a window or a pointer device.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Name = "Hero",
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 1280, Height = 720, Duration = 3 },
        });

        var model = new StudioModel();
        var controller = new StudioController(harness.Cut, model, NullLogger<StudioController>.Instance);
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };

        using var doc = Open(app);
        controller.Attach(doc);

        // Open the project and arm marking, the way the two clicks do.
        doc.OnAction("data-cut-open", _ => true);   // no-op: the controller's handler is already registered
        Activate(doc, app, "data-cut-open", "hero.cut.json");
        SettlePreview(model);
        Activate(doc, app, "data-cut-action", "mark");
        Assert.True(model.Marking,
            $"marking did not arm. selected={model.Selected ?? "(null)"} projects={model.Projects.Count} status={model.Status}");

        model.Time = 1.5;
        model.PendingNote = "logo enters too late";

        // Lay out, then drag across the middle of the preview.
        using (doc.RenderToImage(app.Width, app.Height)) { }
        var stage = LocateSurface(doc.Root, 0, 0, StudioApp.PreviewKey)!.Value;
        var x0 = stage.Left + stage.Width * 0.25f;
        var y0 = stage.Top + stage.Height * 0.30f;
        var x1 = stage.Left + stage.Width * 0.75f;
        var y1 = stage.Top + stage.Height * 0.70f;

        doc.DispatchPointer(1, PointerPhase.Down, x0, y0);
        doc.DispatchPointer(1, PointerPhase.Move, (x0 + x1) / 2, (y0 + y1) / 2);
        doc.DispatchPointer(1, PointerPhase.Move, x1, y1);
        doc.DispatchPointer(1, PointerPhase.Up, x1, y1);

        var saved = harness.Cut.LoadProject("hero");
        var annotation = Assert.Single(saved.Annotations);

        Assert.Equal("logo enters too late", annotation.Note);
        Assert.Equal("reviewer", annotation.Author);
        Assert.Equal(1.5, annotation.Time, 6);

        // The region is where it was drawn, to within a pixel of the preview box.
        Assert.Equal(0.25, annotation.X, 2);
        Assert.Equal(0.30, annotation.Y, 2);
        Assert.Equal(0.50, annotation.W, 2);
        Assert.Equal(0.40, annotation.H, 2);

        // ...and in the project's own pixels, which is what the agent is told.
        var (px, py, pw, ph) = annotation.InPixels(1280, 720);
        Assert.InRange(px, 316, 324);
        Assert.InRange(py, 212, 220);
        Assert.InRange(pw, 636, 644);
        Assert.InRange(ph, 284, 292);

        // The window reflects it, and marking disarms so the next drag is deliberate.
        Assert.Single(model.Annotations);
        Assert.False(model.Marking);
        Assert.Equal(string.Empty, model.PendingNote);
    }

    [Fact]
    public void A_tap_on_the_preview_is_not_an_annotation()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject { Html = Harness.Keyframed, Render = new RenderSettings() });

        var model = new StudioModel();
        var controller = new StudioController(harness.Cut, model, NullLogger<StudioController>.Instance);
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };
        using var doc = Open(app);
        controller.Attach(doc);
        Activate(doc, app, "data-cut-open", "hero.cut.json");
        // Opening starts a preview on a worker, and it reports into the same status strip. Let it
        // land before asserting on that strip, or the assertion races the render.
        SettlePreview(model);
        Activate(doc, app, "data-cut-action", "mark");
        using (doc.RenderToImage(app.Width, app.Height)) { }

        var stage = LocateSurface(doc.Root, 0, 0, StudioApp.PreviewKey)!.Value;
        var x = stage.MidX;
        var y = stage.MidY;

        doc.DispatchPointer(1, PointerPhase.Down, x, y);
        doc.DispatchPointer(1, PointerPhase.Up, x + 1, y + 1);

        Assert.Empty(harness.Cut.LoadProject("hero").Annotations);
        Assert.Contains("tap", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Wait for the background preview to finish. It writes the status strip when it
    /// lands, so anything asserting on that strip has to let it happen first.</summary>
    private static void SettlePreview(StudioModel model)
    {
        // Wait for the first frame, not merely for the Rendering flag: the frame arriving is what
        // settles the window, and asserting before it races the render thread.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!model.HasFrame && DateTime.UtcNow < deadline) Thread.Sleep(5);
        while (model.Rendering && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Thread.Sleep(60);
    }

    /// <summary>Fire the controller's handler for a data- attribute, the way a click on the element
    /// carrying it would. The window's own click path is the engine's; this is about the handler.</summary>
    private static void Activate(CupriDocument doc, StudioApp app, string attribute, string value)
    {
        using (doc.RenderToImage(app.Width, app.Height)) { }
        var box = LocateByAttribute(doc.Root, 0, 0, attribute, value)
            ?? throw new InvalidOperationException($"no element with {attribute}=\"{value}\"");
        var cx = box.MidX;
        var cy = box.MidY;
        doc.DispatchClick(cx, cy);
    }

    /// <summary>An element's box in absolute coordinates - accumulated from the root exactly as
    /// HitTesting does, because a pointer arrives in that space and a parent-relative box does not
    /// describe where anything actually is.</summary>
    private static SKRect? LocateByAttribute(CupriFace.Dom.RenderNode node, float ox, float oy, string attribute, string value)
    {
        var ax = ox + node.X;
        var ay = oy + node.Y;
        if (node.Element?.GetAttribute(attribute) == value) return SKRect.Create(ax, ay, node.Width, node.Height);

        var cx = ax - (node.IsScrollableX ? node.EffectiveScrollX : 0f);
        var cy = ay - (node.IsScrollable ? node.EffectiveScrollY : 0f);
        foreach (var child in node.Children)
            if (LocateByAttribute(child, cx, cy, attribute, value) is { } hit) return hit;
        return null;
    }

    private static SKRect? LocateSurface(CupriFace.Dom.RenderNode node, float ox, float oy, string key)
    {
        var ax = ox + node.X;
        var ay = oy + node.Y;
        if (node.SurfaceKey == key) return SKRect.Create(ax, ay, node.Width, node.Height);

        var cx = ax - (node.IsScrollableX ? node.EffectiveScrollX : 0f);
        var cy = ay - (node.IsScrollable ? node.EffectiveScrollY : 0f);
        foreach (var child in node.Children)
            if (LocateSurface(child, cx, cy, key) is { } hit) return hit;
        return null;
    }

    [Fact]
    public void Play_advances_the_clock_in_real_time_and_stops_at_the_end()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 320, Height = 180, Fps = 30, Duration = 0.6 },
        });

        var model = new StudioModel();
        using var controller = new StudioController(harness.Cut, model, NullLogger<StudioController>.Instance);
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };
        using var doc = Open(app);
        controller.Attach(doc);

        Activate(doc, app, "data-cut-open", "hero.cut.json");
        SettlePreview(model);

        Activate(doc, app, "data-cut-action", "play");
        Assert.True(model.Playing);

        // A short project, so this also proves it stops rather than running past the end or wrapping.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (model.Playing && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.False(model.Playing);
        Assert.Equal(0.6, model.Time, 3);
        Assert.Contains("end", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Marking_a_region_stops_playback_first()
    {
        // You cannot point at a frame that is moving.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 320, Height = 180, Duration = 30 },
        });

        var model = new StudioModel();
        using var controller = new StudioController(harness.Cut, model, NullLogger<StudioController>.Instance);
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };
        using var doc = Open(app);
        controller.Attach(doc);

        Activate(doc, app, "data-cut-open", "hero.cut.json");
        SettlePreview(model);
        Activate(doc, app, "data-cut-action", "play");
        Assert.True(model.Playing);

        Activate(doc, app, "data-cut-action", "mark");

        Assert.False(model.Playing);
        Assert.True(model.Marking);
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
