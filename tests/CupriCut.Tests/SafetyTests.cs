using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// A renderer's risk is what it writes and what it reads. These are the boundaries.
/// </summary>
public sealed class SafetyTests
{
    [Theory]
    [InlineData("../secrets.html")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../outside.html")]
    public void A_composition_path_that_escapes_every_root_is_refused(string path)
    {
        using var harness = new Harness();
        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.ResolveRead(path));
        Assert.Contains("composition root", ex.Message);
    }

    [Fact]
    public void An_absolute_composition_path_outside_the_roots_is_refused()
    {
        using var harness = new Harness();
        Assert.Throws<CutPolicyException>(() => harness.Cut.ResolveRead(Path.Combine(Path.GetTempPath(), "elsewhere.html")));
    }

    [Fact]
    public void A_composition_inside_a_root_resolves()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("motion/spinner.html", Harness.Keyframed);
        var resolved = harness.Cut.ResolveRead(name);
        Assert.True(File.Exists(resolved));
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("../../escape.png")]
    public void An_output_path_that_escapes_the_output_root_is_refused(string path)
    {
        using var harness = new Harness();
        var ex = Assert.Throws<CutPolicyException>(() => harness.Cut.ResolveWrite(path));
        Assert.Contains("OutputRoot", ex.Message);
    }

    [Fact]
    public void Video_off_refuses_before_ffmpeg_is_ever_launched()
    {
        using var harness = new Harness(o => o.EnableVideo = false);
        var ex = Assert.Throws<CutPolicyException>(harness.Cut.EnsureVideoAllowed);
        Assert.Contains("Cut:EnableVideo", ex.Message);
    }

    [Fact]
    public void Alpha_with_a_codec_that_cannot_carry_it_is_refused_rather_than_silently_opaque()
    {
        // The failure mode this prevents: a "transparent" video that is quietly black.
        var ex = Assert.Throws<ArgumentException>(() => VideoEncoder.Resolve("h264", alpha: true));
        Assert.Contains("no alpha channel", ex.Message);
        Assert.Contains("vp9", ex.Message);
    }

    [Fact]
    public void Asking_for_alpha_without_naming_a_codec_picks_one_that_carries_it()
    {
        Assert.True(VideoEncoder.Resolve(null, alpha: true).Alpha);
        Assert.Equal("h264", VideoEncoder.Resolve(null, alpha: false).Name);
    }

    [Fact]
    public void An_unknown_codec_names_the_ones_that_exist()
    {
        var ex = Assert.Throws<ArgumentException>(() => VideoEncoder.Resolve("av1", alpha: false));
        Assert.Contains("h264", ex.Message);
    }

    [Fact]
    public void Gif_builds_a_palette_from_the_frames_rather_than_flattening_to_256_colours()
    {
        // Measured on the revenue-card example: a flat rgb8 conversion made a 16.6 MB posterised
        // file, and the palettegen/paletteuse graph made a 0.6 MB sharp one. Worth a guard.
        var gif = VideoEncoder.Codecs["gif"];
        Assert.Null(gif.PixelFormat);   // paletteuse decides it; passing one as well fights it
        Assert.Contains(gif.ExtraArgs, a => a.Contains("palettegen") && a.Contains("paletteuse"));
    }

    [Fact]
    public void The_content_root_is_the_apps_own_directory_not_the_dotnet_hosts()
    {
        // The bug this guards: the content root was derived from Environment.ProcessPath, which is
        // dotnet.exe when the app runs as `dotnet CupriCut.dll` - what every VS Code launch and
        // `dotnet run` does. It resolved to the SDK install directory, so the app looked for its
        // configuration in Program Files and died trying to create an output folder there.
        // These tests run under the test host, which IS such a case.
        var root = ContentRoot.Locate();

        Assert.False(string.IsNullOrWhiteSpace(root));
        Assert.True(Directory.Exists(root), $"content root '{root}' does not exist");
        Assert.Equal(root, Path.TrimEndingDirectorySeparator(root));       // no trailing separator
        Assert.DoesNotContain("dotnet" + Path.DirectorySeparatorChar + "sdk", root, StringComparison.OrdinalIgnoreCase);

        // And the service must agree with it - they were computed separately, and disagreed.
        using var harness = new Harness();
        Assert.Equal(root, ContentRoot.Locate());
    }

    [Fact]
    public void A_family_no_registered_face_covers_fails_naming_the_family()
    {
        // FontPolicy.RegisteredOnly, always. A silent substitution is the thing being prevented:
        // it renders, it is deterministic, and it is wrong on any machine but this one.
        using var harness = new Harness();
        var name = harness.WriteComposition("missing-font.html", """
            <div>Hello</div>
            <style> div { font-family: "Definitely Not Installed"; } </style>
            """);

        var ex = Record.Exception(() => harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 200, Height = 100, Times = [0] }, _ => { }));

        Assert.NotNull(ex);
        Assert.Contains("Definitely Not Installed", ex.ToString());
    }
}
