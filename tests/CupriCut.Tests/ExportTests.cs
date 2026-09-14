using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The formats, named after the outcome rather than the codec.
///
/// <para>"h264 in an mp4 container at yuv420p" is a true description of what someone wants and a
/// useless way to ask for it. These tests are about the names carrying the right machinery, and
/// about the two formats that are made of nothing but alpha refusing to be written without it.</para>
/// </summary>
public sealed class ExportTests
{
    private static string Place(string name) => Path.Combine(Path.GetTempPath(), "cupricut-export-tests", name);

    [Theory]
    [InlineData("mp4", "h264", ".mp4")]
    [InlineData("h265", "h265", ".mp4")]
    [InlineData("webm", "vp9", ".webm")]
    [InlineData("mov", "prores", ".mov")]
    [InlineData("gif", "gif", ".gif")]
    public void A_format_carries_the_codec_that_outcome_needs(string format, string codec, string extension)
    {
        var target = ExportFormats.Resolve(format, alpha: false, Place);

        Assert.Equal(format, target.Name);
        Assert.Equal(codec, target.Codec.Name);
        Assert.Equal(extension, Path.GetExtension(target.Path));
        Assert.Equal(extension, ExportFormats.Extension(format));
        Assert.Equal(AlphaMode.Embedded, target.AlphaMode);
    }

    [Theory]
    [InlineData("mask", AlphaMode.MaskOnly)]
    [InlineData("matte", AlphaMode.MatteBelow)]
    public void The_alpha_formats_bring_their_own_mode(string format, AlphaMode mode)
    {
        var target = ExportFormats.Resolve(format, alpha: true, Place);

        Assert.Equal(mode, target.AlphaMode);
        // h264 for both, because a mask and a matte are opaque frames and play anywhere.
        Assert.Equal("h264", target.Codec.Name);
        Assert.False(target.Codec.Alpha);
    }

    [Theory]
    [InlineData("mask")]
    [InlineData("matte")]
    public void A_format_made_of_alpha_is_refused_without_alpha(string format)
    {
        // Rather than writing a flat rectangle and leaving someone to key against it.
        var ex = Assert.Throws<ArgumentException>(() => ExportFormats.Resolve(format, alpha: false, Place));

        Assert.Contains("alpha", ex.Message);
        Assert.Contains("alpha:true", ex.Message);
    }

    [Fact]
    public void An_unknown_format_names_the_known_ones()
    {
        var ex = Assert.Throws<ArgumentException>(() => ExportFormats.Resolve("avi", alpha: false, Place));

        Assert.Contains("avi", ex.Message);
        Assert.Contains("mp4", ex.Message);
        Assert.Contains("mask", ex.Message);
        Assert.Throws<ArgumentException>(() => ExportFormats.Extension("avi"));
    }

    [Fact]
    public void Frames_is_a_numbered_pattern_and_says_so()
    {
        var target = ExportFormats.Resolve("frames", alpha: false, Place);

        Assert.True(target.IsSequence);
        Assert.Contains("%05d", target.Path);
        Assert.Contains("sequence", target.Note);
        // rgb24 opaque; the same format with alpha has to keep it.
        Assert.Equal("rgb24", target.Codec.PixelFormat);
        Assert.Equal("rgba", ExportFormats.Resolve("frames", alpha: true, Place).Codec.PixelFormat);
    }

    [Fact]
    public void A_single_file_target_is_not_a_sequence()
    {
        Assert.False(ExportFormats.Resolve("mp4", alpha: false, Place).IsSequence);
    }

    [Fact]
    public void Every_named_format_resolves()
    {
        // So a name can be listed and then not work.
        foreach (var format in ExportFormats.Names)
        {
            var target = ExportFormats.Resolve(format, alpha: true, Place);
            Assert.False(string.IsNullOrWhiteSpace(target.Path));
        }
    }

    [Fact]
    public void Every_named_format_is_described()
    {
        var described = ExportFormats.Describe()
            .Select(d => (string)d.GetType().GetProperty("format")!.GetValue(d)!)
            .ToList();

        Assert.Equal([.. ExportFormats.Names.Order()], [.. described.Order()]);
    }

    // ---- the plan -----------------------------------------------------------------------------

    [Fact]
    public void Asking_for_a_mask_is_asking_for_a_transparent_render()
    {
        // "Give me the mask" is a complete request; making someone also say alpha:true is making
        // them say the same thing twice.
        var plan = ExportFormats.Plan(["mp4", "mask"], asked: null, projectAlpha: false, Place);

        Assert.True(plan.Alpha);
        Assert.Equal(["mp4", "mask"], plan.Targets.Select(t => t.Name));
        Assert.Equal(AlphaMode.MaskOnly, plan.Targets[1].AlphaMode);
    }

    [Fact]
    public void The_alpha_the_targets_were_built_against_is_the_alpha_of_the_render()
    {
        // The bug this exists for: the format table inferred alpha, and then the RAW argument went
        // to the sweep. An export of mp4 + mask rendered opaque, the mask's filter never engaged,
        // and the two files came out byte-identical. Nothing failed - the only symptom was two
        // files of exactly the same size.
        var plan = ExportFormats.Plan(["mp4", "mask"], asked: null, projectAlpha: false, Place);

        // A mask target can only have been built if alpha was on, so the two cannot disagree.
        Assert.True(plan.Alpha);
        Assert.Contains(plan.Targets, t => t.AlphaMode != AlphaMode.Embedded);
    }

    [Fact]
    public void An_explicit_no_alpha_beside_a_mask_is_a_contradiction_and_is_refused()
    {
        // Inference fills a SILENCE. Someone who said alpha:false and then asked for a mask has
        // asked for two incompatible things, and guessing which they meant would be worse.
        var ex = Assert.Throws<ArgumentException>(() =>
            ExportFormats.Plan(["mask"], asked: false, projectAlpha: false, Place));

        Assert.Contains("alpha", ex.Message);
    }

    [Theory]
    [InlineData(null, false, false)]   // nothing said, nothing needed: opaque
    [InlineData(null, true, true)]     // the project remembers alpha
    [InlineData(true, false, true)]    // the caller asked for it
    [InlineData(false, true, false)]   // ...and the caller outranks the project
    public void Alpha_is_resolved_caller_then_format_then_project(bool? asked, bool project, bool expected) =>
        Assert.Equal(expected, ExportFormats.Plan(["mp4"], asked, project, Place).Alpha);

    [Fact]
    public void An_empty_format_list_means_mp4()
    {
        var one = Assert.Single(ExportFormats.Plan([], asked: null, projectAlpha: false, Place).Targets);
        Assert.Equal("mp4", one.Name);
    }

    [Fact]
    public void A_mask_is_the_alpha_channel_and_nothing_else()
    {
        // The filter is the feature: alphaextract to grey, no colour stacked beside it.
        var filter = Filter(AlphaMode.MaskOnly);

        Assert.Contains("alphaextract", filter);
        Assert.Contains("format=gray", filter);
        Assert.DoesNotContain("stack", filter);
    }

    [Theory]
    [InlineData(AlphaMode.MatteBelow, "vstack")]
    [InlineData(AlphaMode.MatteRight, "hstack")]
    public void A_matte_still_stacks_the_colour_beside_the_alpha(AlphaMode mode, string stack)
    {
        var filter = Filter(mode);

        Assert.Contains("alphaextract", filter);
        Assert.Contains(stack, filter);
    }

    [Fact]
    public void A_mask_of_an_opaque_render_is_refused_at_the_codec_too()
    {
        // Two doors into the same mistake - the export format table and the codec resolver - so
        // both of them are shut.
        var ex = Assert.Throws<ArgumentException>(() => VideoEncoder.Resolve("h264", alpha: false, AlphaMode.MaskOnly));
        Assert.Contains("white rectangle", ex.Message);
    }

    /// <summary>The filter graph for a mode, reached the way the encoder reaches it.</summary>
    private static string Filter(AlphaMode mode) =>
        (string)typeof(VideoEncoder)
            .GetMethod("MatteFilter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [mode])!;
}
