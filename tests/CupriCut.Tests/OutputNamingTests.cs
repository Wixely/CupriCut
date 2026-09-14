using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// What a rendered file is called.
///
/// <para>An export of two formats wrote <c>mp4.mp4</c> and <c>mask.mp4</c> into a folder named
/// after the composition. Nothing said what was in them, and the next export silently replaced
/// them - so keeping two takes meant renaming the first by hand. A render is a take, and takes
/// accumulate.</para>
/// </summary>
public sealed class OutputNamingTests
{
    private static readonly DateTimeOffset When = new(2026, 9, 14, 14, 30, 52, TimeSpan.Zero);

    [Fact]
    public void A_chosen_name_carries_the_project_and_the_moment()
    {
        Assert.Equal("hero_20260914-143052.mp4", OutputNaming.File("hero", ".mp4", when: When));
    }

    [Fact]
    public void A_discriminator_goes_last_so_the_name_still_sorts_by_time()
    {
        Assert.Equal("hero_20260914-143052_mask.mp4",
            OutputNaming.File("hero", ".mp4", "mask", When));
    }

    [Fact]
    public void The_stamp_sorts_as_text_and_is_legal_everywhere()
    {
        var earlier = OutputNaming.Stamp(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var later = OutputNaming.Stamp(new DateTimeOffset(2026, 11, 2, 3, 4, 5, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier, later) < 0, $"{earlier} should sort before {later}");

        // Colons are the classic mistake: Windows refuses them outright.
        var bad = later.IndexOfAny(Path.GetInvalidFileNameChars());
        Assert.True(bad < 0, bad < 0 ? "" : $"'{later[bad]}' at {bad} is not legal in a file name");
    }

    [Fact]
    public void A_run_stamps_the_folder_so_the_files_inside_need_not()
    {
        // Opening an export should show hero_mp4.mp4 and hero_mask.mp4, not the same timestamp
        // repeated four times.
        Assert.Equal("hero_20260914-143052", OutputNaming.Run("hero", When));
        Assert.Equal("hero_mask.mp4", OutputNaming.InRun("hero", "mask", ".mp4"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("final", true)]
    public void A_name_the_caller_gave_is_recognised_as_theirs(string? given, bool expected) =>
        Assert.Equal(expected, OutputNaming.Explicit(given));

    [Fact]
    public void Two_runs_a_second_apart_do_not_collide()
    {
        var first = OutputNaming.Run("hero", When);
        var second = OutputNaming.Run("hero", When.AddSeconds(1));

        Assert.NotEqual(first, second);
    }

    // ---- and the tools actually use it ----------------------------------------------------------

    [Fact]
    public void An_unnamed_video_render_is_stamped_and_a_named_one_is_not()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 160, Height = 90, Fps = 5, Duration = 0.4 },
        });

        var stamped = Tools.RenderTools.RenderVideo(harness.Cut, harness.Encoder, "hero.cut.json");
        var named = Tools.RenderTools.RenderVideo(harness.Cut, harness.Encoder, "hero.cut.json", output: "final");

        // The stamp is the DEFAULT...
        Assert.Matches(@"hero_\d{8}-\d{6}\.mp4", stamped);
        // ...and a name the caller gave is used exactly, because something is expecting it.
        Assert.Contains("final.mp4", named);
        Assert.DoesNotMatch(@"final_\d{8}", named);
    }

    [Fact]
    public void An_export_names_every_file_for_the_project_and_the_format()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 160, Height = 90, Fps = 5, Duration = 0.4 },
        });

        Tools.RenderTools.Export(harness.Cut, harness.Encoder, "hero.cut.json", ["mp4", "gif"]);

        var folder = Directory.EnumerateDirectories(Path.Combine(harness.Root, "output")).Single();
        Assert.Matches(@"hero_\d{8}-\d{6}$", Path.GetFileName(folder));

        var files = Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f)).Order().ToArray();
        Assert.Equal(["hero_gif.gif", "hero_mp4.mp4"], files);
    }
}
