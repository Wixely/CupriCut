using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// Transparency, and the difference between asking for it and getting it.
///
/// <para>The codec check alone was never enough. Measured on ffmpeg N-91454: libvpx-vp9 accepts
/// <c>-pix_fmt yuva420p</c>, reports success, and writes plain <c>yuv420p</c> — a silently opaque
/// "transparent" video, which is precisely the outcome the codec check existed to prevent.</para>
/// </summary>
public sealed class AlphaTests
{
    [Fact]
    public void H264_has_no_alpha_channel_and_says_so_rather_than_pretending()
    {
        var ex = Assert.Throws<ArgumentException>(() => VideoEncoder.Resolve("h264", alpha: true));

        Assert.Contains("no alpha channel", ex.Message);
        // ...and it names the way out, both of them.
        Assert.Contains("prores", ex.Message);
        Assert.Contains("matteBelow", ex.Message);
    }

    [Fact]
    public void H264_with_a_matte_is_allowed_because_a_matte_needs_no_alpha_channel()
    {
        // The whole point of a matte: transparency without codec support. This is what makes
        // "h264 with transparency" a real thing rather than a contradiction.
        var codec = VideoEncoder.Resolve("h264", alpha: true, AlphaMode.MatteBelow);
        Assert.Equal("h264", codec.Name);
        Assert.False(codec.Alpha);

        Assert.Equal("h264", VideoEncoder.Resolve("h264", alpha: true, AlphaMode.MatteRight).Name);
        Assert.Equal("gif", VideoEncoder.Resolve("gif", alpha: true, AlphaMode.MatteBelow).Name);
    }

    [Fact]
    public void An_alpha_capable_codec_is_still_chosen_by_default_for_embedded_alpha()
    {
        Assert.True(VideoEncoder.Resolve(null, alpha: true).Alpha);
        Assert.Equal("h264", VideoEncoder.Resolve(null, alpha: false).Name);

        // ...but a matte does not need one, so the ordinary default stands.
        Assert.Equal("h264", VideoEncoder.Resolve(null, alpha: true, AlphaMode.MatteBelow).Name);
    }

    [Theory]
    [InlineData("yuva420p", true)]
    [InlineData("yuva444p10le", true)]
    [InlineData("rgba", true)]
    [InlineData("bgra", true)]
    [InlineData("argb", true)]
    [InlineData("ya8", true)]
    [InlineData("yuv420p", false)]      // the format vp9 silently produced
    [InlineData("yuv444p10le", false)]
    [InlineData("gbrp", false)]
    [InlineData("rgb24", false)]
    public void A_pixel_format_is_judged_by_whether_it_has_an_alpha_component(string format, bool expected) =>
        Assert.Equal(expected, VideoEncoder.HasAlpha(format));

    [Fact]
    public void The_alpha_capable_codecs_are_the_ones_that_actually_have_the_channel()
    {
        // h264 and h265 have no alpha in any profile; vp9 and prores 4444 do. gif's one-bit
        // transparency is not an alpha channel and is not offered as one.
        Assert.False(VideoEncoder.Codecs["h264"].Alpha);
        Assert.False(VideoEncoder.Codecs["h265"].Alpha);
        Assert.False(VideoEncoder.Codecs["gif"].Alpha);
        Assert.True(VideoEncoder.Codecs["vp9"].Alpha);
        Assert.True(VideoEncoder.Codecs["prores"].Alpha);
    }
}
