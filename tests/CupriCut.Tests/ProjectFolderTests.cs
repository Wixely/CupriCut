using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// Projects in folders.
///
/// <para>A flat list is fine for three projects and useless for thirty. A folder here is just a
/// directory under the project root - not an index, not a field in a file. Nothing can therefore
/// get out of step with where the projects actually are, and organising them with a file manager
/// instead works exactly as well.</para>
/// </summary>
public sealed class ProjectFolderTests
{
    private static CutProject Simple() => new() { Html = Harness.Keyframed };

    [Fact]
    public void A_project_can_be_saved_into_a_folder_and_read_back_by_that_path()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("promos/spring/hero", Simple());

        Assert.Equal(["promos/spring/hero.cut.json"], harness.Cut.ListProjects());

        // ...and the path it was listed as is a path it can be loaded by.
        Assert.NotNull(harness.Cut.LoadProject("promos/spring/hero.cut.json"));
        Assert.NotNull(harness.Cut.LoadProject("promos/spring/hero"));
    }

    [Fact]
    public void Folders_are_discovered_from_where_the_projects_are()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("promos/hero", Simple());
        harness.Cut.SaveProject("promos/spring/teaser", Simple());
        harness.Cut.SaveProject("loose", Simple());

        Assert.Equal(["promos", "promos/spring"], harness.Cut.ListFolders());
    }

    [Fact]
    public void An_empty_folder_is_still_a_folder()
    {
        // Someone made it. It being unfilled is not a reason to forget it.
        using var harness = new Harness();
        Assert.Equal("drafts", harness.Cut.CreateFolder("drafts"));
        Assert.Contains("drafts", harness.Cut.ListFolders());
        Assert.Empty(harness.Cut.ListProjects());
    }

    [Fact]
    public void Moving_to_a_folder_keeps_the_name()
    {
        // "promos" means into promos, not rename to promos - moving is the common case and should
        // not need the name said twice.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", Simple());

        Assert.Equal("promos/hero.cut.json", harness.Cut.MoveProject("hero", "promos"));
        Assert.Equal(["promos/hero.cut.json"], harness.Cut.ListProjects());
    }

    [Fact]
    public void Naming_a_project_at_the_far_end_is_a_rename()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", Simple());

        Assert.Equal("promos/opener.cut.json", harness.Cut.MoveProject("hero", "promos/opener.cut.json"));
        Assert.Equal(["promos/opener.cut.json"], harness.Cut.ListProjects());
    }

    [Fact]
    public void Moving_out_of_a_folder_back_to_the_root()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("promos/hero", Simple());

        Assert.Equal("hero.cut.json", harness.Cut.MoveProject("promos/hero", ""));
        Assert.Equal(["hero.cut.json"], harness.Cut.ListProjects());
    }

    [Fact]
    public void The_annotations_travel_with_it()
    {
        // A move is a move, not a re-save - whatever was in the file is what is in the file.
        using var harness = new Harness();
        var project = Simple();
        project.Annotations.Add(new Annotation { Note = "logo enters too late", Time = 1.2 });
        harness.Cut.SaveProject("hero", project);

        harness.Cut.MoveProject("hero", "promos");

        var moved = harness.Cut.LoadProject("promos/hero");
        Assert.Equal("logo enters too late", Assert.Single(moved.Annotations).Note);
    }

    [Fact]
    public void Moving_onto_an_existing_project_is_refused()
    {
        // Two projects with the same name is a thing to be told about, not to resolve by
        // destroying one of them.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", Simple());
        harness.Cut.SaveProject("promos/hero", Simple());

        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.MoveProject("hero", "promos"));

        Assert.Contains("already a project", ex.Message);
        // ...and both are still there.
        Assert.Equal(2, harness.Cut.ListProjects().Count);
    }

    [Fact]
    public void Moving_a_project_to_where_it_already_is_does_nothing_and_says_so()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("promos/hero", Simple());

        Assert.Equal("promos/hero.cut.json", harness.Cut.MoveProject("promos/hero", "promos"));
        Assert.Equal(["promos/hero.cut.json"], harness.Cut.ListProjects());
    }

    // ---- the root is the boundary ---------------------------------------------------------------

    [Theory]
    [InlineData("../escape")]
    [InlineData("promos/../../escape")]
    public void A_folder_outside_the_project_root_is_refused(string folder)
    {
        using var harness = new Harness();
        Assert.Throws<CutPolicyException>(() => harness.Cut.CreateFolder(folder));
    }

    [Fact]
    public void Moving_a_project_outside_the_project_root_is_refused()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", Simple());

        Assert.Throws<CutPolicyException>(() => harness.Cut.MoveProject("hero", "../escape"));
        Assert.Equal(["hero.cut.json"], harness.Cut.ListProjects());
    }

    [Fact]
    public void A_folder_that_names_a_project_is_refused()
    {
        using var harness = new Harness();
        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.CreateFolder("hero.cut.json"));
        Assert.Contains("names a project", ex.Message);
    }

    [Fact]
    public void Moving_something_that_is_not_there_says_so()
    {
        using var harness = new Harness();
        Assert.Throws<FileNotFoundException>(() => harness.Cut.MoveProject("nope", "promos"));
    }
}
