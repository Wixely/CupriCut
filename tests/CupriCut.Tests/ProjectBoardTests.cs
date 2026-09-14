using CupriCut.Gui;
using CupriCut.Services;
using CupriFace;
using CupriFace.Dom;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// Dragging a project into a folder.
///
/// <para>The gesture is the engine's - <c>cupri-board</c> exists for exactly this - so what is
/// worth testing is the part that is ours: that the drop resolves to the right destination, that
/// the file actually moves, and that the window does not end up pointing at a project that is no
/// longer where it thinks it is.</para>
///
/// <para>The drop is raised directly rather than by synthesising a pointer drag. Dispatching a real
/// drag would be testing the engine's gesture recogniser, which is not this repository's to get
/// right, and would break every time its thresholds changed.</para>
/// </summary>
public sealed class ProjectBoardTests
{
    private static CutProject Simple() => new() { Html = Harness.Keyframed, Name = "Hero card" };

    private sealed record Fixture(Harness Harness, StudioModel Model, StudioController Controller, CupriDocument Doc, StudioApp App)
        : IDisposable
    {
        public void Dispose()
        {
            Controller.Dispose();
            Doc.Dispose();
            Harness.Dispose();
        }
    }

    private static Fixture Open()
    {
        var harness = new Harness();
        var model = new StudioModel();
        var controller = new StudioController(harness.Cut, harness.Encoder, model, NullLogger<StudioController>.Instance);
        var app = new StudioApp(model) { FontSources = [.. FontFiles()] };
        var doc = CupriDocument.Load(app.Html, app.Css).UseComponents(app.Components);
        foreach (var font in app.Fonts) doc.LoadFont(font);
        doc.Bind(app.Model!);
        return new Fixture(harness, model, controller, doc, app);
    }

    [Fact]
    public void Every_folder_gets_a_column_including_the_empty_ones_and_the_top_level()
    {
        // A folder with nothing in it is still somewhere to drop something, and a column is the
        // only place a drop can land.
        using var f = Open();
        f.Harness.Cut.SaveProject("hero", Simple());
        f.Harness.Cut.CreateFolder("promos");
        f.Controller.Attach(f.Doc);

        Assert.Equal(["", "promos"], f.Model.Folders.Select(c => c.Path));
        Assert.Equal("Top level", f.Model.Folders[0].Title);
        Assert.Equal("hero.cut.json", Assert.Single(f.Model.Folders[0].Projects).File);
        Assert.Empty(f.Model.Folders[1].Projects);
        Assert.Equal("", f.Model.Folders[1].EmptyClass);          // the "drop here" hint shows
    }

    [Fact]
    public void Dropping_a_card_on_another_folder_moves_the_file()
    {
        using var f = Open();
        f.Harness.Cut.SaveProject("hero", Simple());
        f.Harness.Cut.CreateFolder("promos");
        f.Controller.Attach(f.Doc);

        Drop(f, from: "", to: "promos", index: 0);

        Assert.Equal(["promos/hero.cut.json"], f.Harness.Cut.ListProjects());
        Assert.Contains("Moved Hero card to promos", f.Model.Status);

        // ...and the board says so too, without being reloaded by hand.
        Assert.Empty(f.Model.Folders.Single(c => c.Path == "").Projects);
        Assert.Single(f.Model.Folders.Single(c => c.Path == "promos").Projects);
    }

    [Fact]
    public void Moving_the_open_project_follows_it()
    {
        // The preview addresses a project by path. Moving it under the window's feet would leave
        // the render thread pointing at a file that is no longer there.
        using var f = Open();
        f.Harness.Cut.SaveProject("hero", Simple());
        f.Harness.Cut.CreateFolder("promos");
        f.Controller.Attach(f.Doc);
        f.Model.Selected = "hero.cut.json";

        Drop(f, from: "", to: "promos", index: 0);

        Assert.Equal("promos/hero.cut.json", f.Model.Selected);
    }

    [Fact]
    public void Dropping_back_onto_the_top_level_works_too()
    {
        using var f = Open();
        f.Harness.Cut.SaveProject("promos/hero", Simple());
        f.Controller.Attach(f.Doc);

        Drop(f, from: "promos", to: "", index: 0);

        Assert.Equal(["hero.cut.json"], f.Harness.Cut.ListProjects());
    }

    [Fact]
    public void Reordering_within_a_folder_is_declined_rather_than_pretended()
    {
        // Projects are listed by name and there is nowhere to record a hand-made order, so
        // accepting the drag would show a reordering that vanished on the next refresh.
        using var f = Open();
        f.Harness.Cut.SaveProject("hero", Simple());
        f.Harness.Cut.SaveProject("teaser", Simple());
        f.Controller.Attach(f.Doc);

        Drop(f, from: "", to: "", index: 0);

        Assert.Equal(2, f.Harness.Cut.ListProjects().Count);
        Assert.Contains("listed by name", f.Model.Status);
    }

    [Fact]
    public void A_move_onto_an_existing_name_is_refused_and_said_out_loud()
    {
        using var f = Open();
        f.Harness.Cut.SaveProject("hero", Simple());
        f.Harness.Cut.SaveProject("promos/hero", Simple());
        f.Controller.Attach(f.Doc);

        Drop(f, from: "", to: "promos", index: 0);

        // Both survive, and the window says why nothing happened.
        Assert.Equal(2, f.Harness.Cut.ListProjects().Count);
        Assert.Contains("already a project", f.Model.Status);
    }

    [Fact]
    public void A_new_folder_appears_as_a_column_to_drop_into()
    {
        using var f = Open();
        f.Controller.Attach(f.Doc);
        f.Model.NewFolder = "  spring  ";

        Activate(f, "new-folder");

        Assert.Contains("spring", f.Model.Folders.Select(c => c.Path));
        Assert.Equal(string.Empty, f.Model.NewFolder);      // the field clears
        Assert.Contains("Created spring", f.Model.Status);
    }

    [Fact]
    public void A_nameless_new_folder_says_so_rather_than_making_one()
    {
        using var f = Open();
        f.Controller.Attach(f.Doc);
        f.Model.NewFolder = "   ";

        Activate(f, "new-folder");

        Assert.Empty(f.Harness.Cut.ListFolders());
        Assert.Contains("Name the folder", f.Model.Status);
    }

    [Fact]
    public void A_populated_board_for_the_documentation()
    {
        // Writes projects.png beside the test binary. The board is the one screen that is only
        // meaningful with several folders in it, and nobody has several folders on a fresh install.
        using var f = Open();
        f.Harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed, Name = "Hero card",
            Render = new RenderSettings { Width = 1280, Height = 720, Fps = 30, Duration = 3 },
        });
        f.Harness.Cut.SaveProject("promos/launch-teaser", new CutProject
        {
            Html = Harness.Keyframed, Name = "Launch teaser",
            Render = new RenderSettings { Width = 1920, Height = 1080, Fps = 60, Duration = 12 },
        });
        f.Harness.Cut.SaveProject("promos/spring/lower-third", new CutProject
        {
            Html = Harness.Keyframed, Name = "Lower third",
            Render = new RenderSettings { Width = 1280, Height = 720, Fps = 30, Duration = 3 },
        });
        f.Harness.Cut.CreateFolder("archive");
        f.Controller.Attach(f.Doc);
        Show(f);

        using var image = f.Doc.RenderToImage(f.App.Width, f.App.Height);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "projects.png"));
        data.SaveTo(file);

        Assert.Equal(["", "archive", "promos", "promos/spring"], f.Model.Folders.Select(c => c.Path));
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Raise the drop the engine would raise, from the folder columns in the live DOM.</summary>
    private static void Drop(Fixture f, string from, string to, int index)
    {
        Show(f);

        var source = Column(f.Doc.Root, from) ?? throw new InvalidOperationException($"no column for '{from}'");
        var target = Column(f.Doc.Root, to) ?? throw new InvalidOperationException($"no column for '{to}'");

        f.Controller.OnProjectDropped(new CupriDocument.ReorderEvent(source, index, index, target));
    }

    private static void Activate(Fixture f, string action)
    {
        Show(f);

        var box = Locate(f.Doc.Root, 0, 0, "data-cut-action", action)
            ?? throw new InvalidOperationException($"no element with data-cut-action=\"{action}\"");
        Assert.True(box.Width > 0 && box.Height > 0, $"{action} laid out at {box.Width}x{box.Height}");
        f.Doc.DispatchClick(box.MidX, box.MidY);
    }

    /// <summary>Put the window on the projects page and rebuild the tree from the model.
    ///
    /// <para>A hidden page is not merely invisible - its subtree is not built, so nothing in it can
    /// be found or clicked. And rendering does not re-read the model; the desktop host re-binds on
    /// its own timer, which is why a live window keeps up.</para></summary>
    private static void Show(Fixture f)
    {
        f.Model.View = "projects";
        f.Doc.Bind(f.App.Model!);
        using (f.Doc.RenderToImage(f.App.Width, f.App.Height)) { }
    }

    private static SkiaSharp.SKRect? Locate(RenderNode node, float ox, float oy, string attribute, string value)
    {
        var x = ox + node.X;
        var y = oy + node.Y;
        if (node.Element?.GetAttribute(attribute) == value)
            return new SkiaSharp.SKRect(x, y, x + node.Width, y + node.Height);
        foreach (var child in node.Children)
            if (Locate(child, x, y, attribute, value) is { } hit) return hit;
        return null;
    }

    private static AngleSharp.Dom.IElement? Column(RenderNode node, string folder)
    {
        if (node.Element?.GetAttribute(StudioController.FolderAttribute) == folder) return node.Element;
        foreach (var child in node.Children)
            if (Column(child, folder) is { } hit) return hit;
        return null;
    }

    private static IEnumerable<CupriFace.Resources.CupriSource> FontFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fonts"), "*.ttf")
            .Order(StringComparer.Ordinal)
            .Select(CupriFace.Resources.CupriSource.File);
}
