using CupriCut.Services;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// Reading footage: where the cuts are, and where it is calm enough to put words over.
///
/// <para>Built to order, for the same reason the audio tests are. A test over real footage can
/// only assert that the answer did not change; a cut placed at frame 30 either comes back at frame
/// 30 or does not.</para>
/// </summary>
public class VideoCueTests(ITestOutputHelper output)
{
    private const int Size = VideoCues.Width * VideoCues.Height;

    [Fact]
    public void A_cut_is_found_where_it_was_made()
    {
        // Two shots, one cut. Nothing else moves at all.
        var frames = Shot(30, 40).Concat(Shot(30, 200)).ToList();

        var analysis = VideoCues.Analyse(frames, fps: 30);
        var cut = Assert.Single(analysis.Of(CueKind.SceneChange));

        Assert.Equal(30, cut.Frame);
        Assert.Equal(1.0, cut.At, 3);
    }

    [Fact]
    public void Several_cuts_come_back_in_order()
    {
        var frames = Shot(20, 30).Concat(Shot(20, 190)).Concat(Shot(20, 60)).Concat(Shot(20, 230)).ToList();

        var cuts = VideoCues.Analyse(frames, fps: 20).Of(CueKind.SceneChange).ToList();

        output.WriteLine(string.Join("\n", cuts.Select(c => c.Describe())));
        Assert.Equal([20, 40, 60], cuts.Select(c => c.Frame));
    }

    [Fact]
    public void A_pan_is_not_a_cut_however_fast_it_is()
    {
        // The failure that matters. A whip pan changes every pixel too - but gradually, over
        // several frames, and a detector that cannot tell the difference cuts on camera movement.
        var frames = new List<byte[]>();
        for (var f = 0; f < 60; f++) frames.Add(Gradient(offset: f * 3));

        var analysis = VideoCues.Analyse(frames, fps: 30);

        output.WriteLine($"peak motion across the pan: {analysis.Motion.Max()}");
        Assert.Empty(analysis.Of(CueKind.SceneChange));
    }

    [Fact]
    public void A_still_shot_has_no_cuts_and_no_motion()
    {
        var analysis = VideoCues.Analyse(Shot(40, 128), fps: 30);

        Assert.Empty(analysis.Of(CueKind.SceneChange));
        Assert.All(analysis.Motion, m => Assert.Equal(0, m));
    }

    [Fact]
    public void The_first_frame_is_not_a_cut()
    {
        // It has no predecessor. The start of a clip is where the clip starts, not a cut.
        var analysis = VideoCues.Analyse(Shot(20, 200), fps: 30);

        Assert.DoesNotContain(analysis.Of(CueKind.SceneChange), c => c.Frame == 0);
    }

    [Fact]
    public void Luminance_says_how_bright_each_frame_is()
    {
        // What decides whether a caption over it should be light or dark.
        var frames = Shot(10, 0).Concat(Shot(10, 255)).ToList();

        var analysis = VideoCues.Analyse(frames, fps: 10);

        Assert.Equal(0, analysis.Luminance[5]);
        Assert.Equal(1, analysis.Luminance[15]);
        Assert.Equal(frames.Count, analysis.Luminance.Count);
    }

    [Fact]
    public void The_calmest_window_is_the_stillest_stretch()
    {
        // One second of chaos, then two of stillness, at 10 fps.
        var frames = new List<byte[]>();
        var noise = new Random(3);
        for (var f = 0; f < 10; f++) frames.Add(Noise(noise));
        frames.AddRange(Shot(20, 128));

        var analysis = VideoCues.Analyse(frames, fps: 10);
        var (at, motion) = analysis.CalmestWindow(seconds: 1);

        output.WriteLine($"calmest second starts at {at}s with motion {motion}");
        Assert.True(at >= 1.0, $"expected the still half, got {at}s");
        Assert.True(motion < 0.01, $"the still stretch should be near zero, was {motion}");
    }

    [Fact]
    public void The_calmest_window_never_straddles_a_cut()
    {
        // Two still shots with a cut between them. Both halves are perfectly calm, so a detector
        // that only averaged motion would happily choose a window spanning the cut - and the
        // caption would begin over one shot and end over another.
        var frames = Shot(15, 40).Concat(Shot(15, 210)).ToList();

        var analysis = VideoCues.Analyse(frames, fps: 10);
        var cut = Assert.Single(analysis.Of(CueKind.SceneChange));
        var (at, _) = analysis.CalmestWindow(seconds: 1);

        var start = (int)Math.Round(at * 10);
        output.WriteLine($"cut at frame {cut.Frame}, window frames {start}..{start + 10}");

        Assert.True(start >= cut.Frame || start + 10 <= cut.Frame,
            $"window {start}..{start + 10} straddles the cut at {cut.Frame}");
    }

    [Fact]
    public void A_window_longer_than_the_footage_says_so_rather_than_guessing()
    {
        var analysis = VideoCues.Analyse(Shot(10, 128), fps: 10);

        Assert.Equal(-1, analysis.CalmestWindow(seconds: 5).At);
    }

    [Fact]
    public void Footage_of_one_frame_does_not_throw()
    {
        var analysis = VideoCues.Analyse(Shot(1, 128), fps: 30);

        Assert.Empty(analysis.Cues);
        Assert.Single(analysis.Luminance);
    }

    // ---- footage ------------------------------------------------------------------------------

    /// <summary>N frames of one flat brightness.</summary>
    private static List<byte[]> Shot(int count, byte level)
    {
        var frames = new List<byte[]>(count);
        for (var f = 0; f < count; f++)
        {
            var frame = new byte[Size];
            Array.Fill(frame, level);
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>A horizontal ramp, shifted - which is what a pan looks like at this size.</summary>
    private static byte[] Gradient(int offset)
    {
        var frame = new byte[Size];
        for (var y = 0; y < VideoCues.Height; y++)
        for (var x = 0; x < VideoCues.Width; x++)
            frame[y * VideoCues.Width + x] = (byte)(((x + offset) * 8) % 256);
        return frame;
    }

    private static byte[] Noise(Random random)
    {
        var frame = new byte[Size];
        random.NextBytes(frame);
        return frame;
    }
}
