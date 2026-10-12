using CupriCut.Gui;
using CupriCut.Services;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Interaction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// A file dragged onto the studio window from the OS.
///
/// <para><b>Why there is a test file for this at all.</b> The plan recorded that dragging an
/// <c>.html</c> in from a file manager was impossible and had not been faked - the engine's desktop
/// host surfaced no drop event, so no application built on it could accept one. That was raised
/// upstream as <see href="https://github.com/Wixely/CupriFace/issues/182">CupriFace#182</see> and
/// it is fixed, and this is the half of the refusal that could then be withdrawn. Dragging OUT is
/// still not possible and is still not faked: it needs a platform drag source and a data promise,
/// and "Open in file manager" remains the honest substitute.</para>
///
/// <para><b>The gesture is scripted, and that is what makes it testable.</b> The engine ships a
/// <c>DropDriver</c> for exactly this reason - a gesture that can be received but not scripted
/// cannot be regression-tested, and a drop handler is the kind of code that rots quietly, because
/// it runs when somebody drags something in, which is never during a test run. The driver is not a
/// stub standing in for the real thing: a dropped file in a browser genuinely is bytes with a
/// name, and only the desktop hosts differ, by being lazier.</para>
///
/// <para>Two layers, tested separately on purpose. <see cref="Dropped"/> is the classification and
/// the import and needs no window at all; the controller is one handler that routes a
/// <c>FileDropEvent</c> into it and puts the answer in the status bar.</para>
/// </summary>
public sealed class FileDropTests
{
    /// <summary>A 4x4 solid red PNG, generated rather than pasted.</summary>
    private const string RedDot =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGN4oKAARwzEcQDgQxIBzrTn9gAAAABJRU5ErkJggg==";

    // ---- the import, with no window ---------------------------------------------------------

    [Fact]
    public void A_dropped_html_becomes_a_project()
    {
        using var harness = new Harness();
        var file = Outside(harness, "hero.html", Harness.Keyframed);

        var r = Dropped.Accept(harness.Cut, file);

        Assert.Equal(DropKind.Composition, r.Kind);
        Assert.Equal("hero.cut.json", r.Name);
        Assert.Equal(["hero.cut.json"], harness.Cut.ListProjects());

        // Loadable by the ordinary reader, which is the whole claim - it is a project now, not a
        // copy of a file that happens to live in the project directory.
        var project = harness.Cut.LoadProject("hero");
        Assert.Contains(".bar", project.Html);
    }

    [Fact]
    public void A_dropped_file_may_live_anywhere_and_the_write_surface_does_not_move()
    {
        // The one place in this codebase that reads outside Cut:CompositionRoots, and the reason
        // is the gesture: a person at this machine pointed at their own file. What it does NOT do
        // is widen what may be written - the import goes through SaveProject into the project
        // root, exactly where save_project puts things.
        using var harness = new Harness();
        var file = Outside(harness, "elsewhere.html", Harness.Keyframed);

        // Proof the path really is outside the read roots: the agent-facing resolver refuses it.
        Assert.Throws<CutPolicyException>(() => harness.Cut.ResolveRead(file));

        var r = Dropped.Accept(harness.Cut, file);

        Assert.True(r.Accepted);
        Assert.StartsWith(harness.Cut.ProjectRoot, r.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_assets_beside_a_dropped_composition_come_with_it()
    {
        // What makes this an import rather than a copy that only works in the folder it came
        // from. The markup is NOT rewritten: a project resolves an asset by looking its source
        // string up in its own dictionary, so src="logo.png" keeps working by name.
        using var harness = new Harness();
        var directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cupricut-drop-{Guid.NewGuid():N}")).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "logo.png"), Convert.FromBase64String(RedDot));
            var file = Path.Combine(directory, "card.html");
            File.WriteAllText(file,
                "<cupri-image src=\"logo.png\" style=\"width:80px;height:80px\"></cupri-image>");

            var r = Dropped.Accept(harness.Cut, file);

            Assert.Equal(["logo.png"], r.Inlined);
            Assert.Empty(r.Missing);

            var project = harness.Cut.LoadProject("card");
            Assert.Contains("src=\"logo.png\"", project.Html);
            Assert.StartsWith("data:image/png;base64,", project.Assets["logo.png"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_source_that_was_not_beside_it_is_named_rather_than_dropped_in_silence()
    {
        // A composition missing its logo renders perfectly and looks wrong, which is the worst
        // shape a failure can take here. So the import says which sources it could not find, in
        // the answer and in the project's own notes - and the markup keeps referring to them by
        // name, so dropping the file in afterwards fills the gap.
        using var harness = new Harness();
        var file = Outside(harness, "card.html",
            "<cupri-image src=\"missing.png\" style=\"width:80px;height:80px\"></cupri-image>");

        var r = Dropped.Accept(harness.Cut, file);

        Assert.Equal(["missing.png"], r.Missing);
        Assert.Contains(harness.Cut.LoadProject("card").Meta.Notes,
            n => n.Contains("missing.png", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dropped_image_is_inlined_into_the_project_that_is_open()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject { Html = Harness.Keyframed, Name = "Hero" });
        var image = Outside(harness, "logo.png", null, Convert.FromBase64String(RedDot));

        var r = Dropped.Accept(harness.Cut, image, openProject: "hero");

        Assert.Equal(DropKind.Asset, r.Kind);
        Assert.StartsWith("data:image/png;base64,", harness.Cut.LoadProject("hero").Assets["logo.png"]);

        // The message says how to use what was just added, because an asset nothing refers to is
        // bytes in a file doing nothing.
        Assert.Contains("cupri-image", r.Message);
    }

    [Fact]
    public void An_image_with_nothing_open_is_refused_and_says_why()
    {
        using var harness = new Harness();
        var image = Outside(harness, "logo.png", null, Convert.FromBase64String(RedDot));

        var r = Dropped.Accept(harness.Cut, image);

        Assert.False(r.Accepted);
        Assert.Contains("Open a project first", r.Message);
        Assert.Empty(harness.Cut.ListProjects());
    }

    [Fact]
    public void A_dropped_project_lands_in_the_folder_it_was_dropped_on()
    {
        using var harness = new Harness();
        harness.Cut.CreateFolder("promos");
        var file = Outside(harness, "teaser.cut.json",
            new CutProject { Html = Harness.Keyframed, Name = "Teaser" }.ToJson());

        var r = Dropped.Accept(harness.Cut, file, folder: "promos");

        Assert.Equal(DropKind.Project, r.Kind);
        Assert.Equal(["promos/teaser.cut.json"], harness.Cut.ListProjects());
    }

    [Fact]
    public void A_name_already_taken_is_numbered_rather_than_overwritten()
    {
        // A drop is one gesture with no confirmation step, so the obvious way to lose an
        // afternoon's work is to drag in a file whose name matches something already saved.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject { Html = "<div>original</div>", Name = "Original" });
        var file = Outside(harness, "hero.html", Harness.Keyframed);

        var r = Dropped.Accept(harness.Cut, file);

        Assert.Equal("hero-2.cut.json", r.Name);
        Assert.Contains("original", harness.Cut.LoadProject("hero").Html);
    }

    [Fact]
    public void A_folder_is_refused_by_name()
    {
        // Dragging a folder onto a window is an ordinary thing to try, and a folder has no bytes.
        using var harness = new Harness();
        var directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cupricut-folder-{Guid.NewGuid():N}")).FullName;
        try
        {
            var r = Dropped.Accept(harness.Cut, directory);

            Assert.False(r.Accepted);
            Assert.Contains("is a folder", r.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Something_CupriCut_does_not_take_is_refused_naming_what_would_work()
    {
        using var harness = new Harness();
        var file = Outside(harness, "notes.txt", "not a composition");

        var r = Dropped.Accept(harness.Cut, file);

        Assert.False(r.Accepted);
        Assert.Contains(".html", r.Message);
        Assert.Contains(".cut.json", r.Message);
    }

    [Fact]
    public void A_composition_carrying_script_is_imported_and_told_about_translate()
    {
        // It is imported rather than refused - the markup and the CSS are real and worth having -
        // but the note is in the file, because the person who drops a browser block and the person
        // who wonders why it does not move are often the same person a week apart.
        using var harness = new Harness();
        var file = Outside(harness, "browser.html",
            "<div class=\"card\">x</div><script>gsap.timeline().from('.card',{opacity:0});</script>");

        var r = Dropped.Accept(harness.Cut, file);

        Assert.True(r.Accepted);
        Assert.Contains(harness.Cut.LoadProject("browser").Meta.Notes,
            n => n.Contains("translate", StringComparison.Ordinal));
    }

    [Fact]
    public void One_bad_file_does_not_lose_the_rest_of_the_gesture()
    {
        using var harness = new Harness();
        var good = Outside(harness, "hero.html", Harness.Keyframed);
        var bad = Outside(harness, "notes.txt", "nope");

        var results = Dropped.AcceptAll(harness.Cut, [bad, good]);

        Assert.False(results[0].Accepted);
        Assert.True(results[1].Accepted);
        Assert.Equal(["hero.cut.json"], harness.Cut.ListProjects());
    }

    // ---- through the window, as the engine raises it -----------------------------------------

    [Fact]
    public void A_drop_on_the_stage_imports_and_opens_it()
    {
        using var f = OpenWindow();
        var file = Outside(f.Harness, "hero.html", Harness.Keyframed);
        f.Controller.Attach(f.Doc);

        // Laid out first, because a drop is resolved by hit-testing the point.
        using (f.Doc.RenderToImage(f.App.Width, f.App.Height)) { }
        var stage = Box(f.Doc.Root, 0, 0, StudioApp.MarkAttribute, "preview");
        Assert.NotNull(stage);

        var dropped = new DropDriver(f.Doc)
            .Drop(stage!.Value.MidX, stage.Value.MidY, DroppedFile.FromPath(file));

        Assert.True(dropped, "the document should report that the drop was handled");
        Assert.Equal(["hero.cut.json"], f.Harness.Cut.ListProjects());

        // A drop lands you on the thing you dropped.
        Assert.Equal("hero.cut.json", f.Model.Selected);
        Assert.Contains("hero", f.Model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_drop_on_a_folder_column_imports_into_that_folder()
    {
        using var f = OpenWindow();
        f.Harness.Cut.CreateFolder("promos");
        var file = Outside(f.Harness, "teaser.html", Harness.Keyframed);
        f.Controller.Attach(f.Doc);
        f.Model.View = "projects";                     // the board is only laid out on its own page

        using (f.Doc.RenderToImage(f.App.Width, f.App.Height)) { }
        var column = Box(f.Doc.Root, 0, 0, StudioController.FolderAttribute, "promos");
        Assert.NotNull(column);

        new DropDriver(f.Doc).Drop(column!.Value.MidX, column.Value.MidY, DroppedFile.FromPath(file));

        Assert.Equal(["promos/teaser.cut.json"], f.Harness.Cut.ListProjects());
    }

    [Fact]
    public void The_element_the_drop_landed_on_is_the_one_carrying_the_marker()
    {
        // The same guard the annotation overlay needed, for the same reason. The engine walks UP
        // from the hit node looking for the drop class, so whatever sits under the preview centre
        // must still lead to an element carrying it - a change to the markup that nests something
        // new over the stage would otherwise break the drop in silence.
        using var f = OpenWindow();
        f.Controller.Attach(f.Doc);
        using (f.Doc.RenderToImage(f.App.Width, f.App.Height)) { }

        var stage = Box(f.Doc.Root, 0, 0, StudioApp.MarkAttribute, "preview");
        Assert.NotNull(stage);

        var hit = HitTesting.HitTest(f.Doc.Root, stage!.Value.MidX, stage.Value.MidY);
        Assert.NotNull(hit);

        var node = hit;
        while (node is not null && node.Element?.ClassList.Contains(StudioApp.DropClass) != true)
            node = node.Parent;

        Assert.NotNull(node);
    }

    [Fact]
    public void A_dropped_folder_is_reported_rather_than_ignored()
    {
        using var f = OpenWindow();
        f.Controller.Attach(f.Doc);
        using (f.Doc.RenderToImage(f.App.Width, f.App.Height)) { }
        var stage = Box(f.Doc.Root, 0, 0, StudioApp.MarkAttribute, "preview")!.Value;

        var directory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cupricut-folder-{Guid.NewGuid():N}")).FullName;
        try
        {
            new DropDriver(f.Doc).Drop(stage.MidX, stage.MidY, DroppedFile.FromPath(directory));
            Assert.Contains("folder", f.Model.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_document_with_no_handler_tells_the_host_not_to_accept_a_drag()
    {
        // The engine asks before telling the platform a drop is welcome, so a document that wants
        // no files does not show a drop cursor. Worth pinning from this side: the handler being
        // registered in Attach is what makes the window accept anything at all.
        using var f = OpenWindow();
        Assert.False(f.Doc.AcceptsFileDrop);

        f.Controller.Attach(f.Doc);

        Assert.True(f.Doc.AcceptsFileDrop);
    }

    // ---- plumbing ----------------------------------------------------------------------------

    /// <summary>
    /// Write a file OUTSIDE every configured root, which is where a dropped file comes from.
    ///
    /// <para>Inside the harness's own scratch directory but not inside any of its roots - the
    /// composition roots are <c>compositions/</c> and <c>projects/</c> under it, and this is
    /// neither - so it is genuinely unreachable by <c>ResolveRead</c> and still goes away when the
    /// harness does. A sibling of the scratch directory would have leaked a folder per test.</para>
    /// </summary>
    private static string Outside(Harness harness, string name, string? text, byte[]? bytes = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(harness.Root, "dropped")).FullName;
        var path = Path.Combine(directory, name);
        if (bytes is not null) File.WriteAllBytes(path, bytes);
        else File.WriteAllText(path, text ?? string.Empty);
        return path;
    }

    private sealed record Fixture(
        Harness Harness, StudioModel Model, StudioController Controller, CupriDocument Doc, StudioApp App)
        : IDisposable
    {
        public void Dispose()
        {
            Controller.Dispose();
            Doc.Dispose();
            Harness.Dispose();
        }
    }

    private static Fixture OpenWindow()
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

    private static SkiaSharp.SKRect? Box(RenderNode node, float ox, float oy, string attribute, string value)
    {
        // The origin is accumulated down the tree because RenderNode.X/Y are PARENT-relative -
        // the same thing the annotation maths had to learn the hard way.
        var x = ox + node.X;
        var y = oy + node.Y;
        if (node.Element?.GetAttribute(attribute) == value)
            return new SkiaSharp.SKRect(x, y, x + node.Width, y + node.Height);
        foreach (var child in node.Children)
            if (Box(child, x, y, attribute, value) is { } hit) return hit;
        return null;
    }

    private static IEnumerable<CupriFace.Resources.CupriSource> FontFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fonts"), "*.ttf")
            .Order(StringComparer.Ordinal)
            .Select(CupriFace.Resources.CupriSource.File);
}
