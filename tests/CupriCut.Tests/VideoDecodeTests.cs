using System.Diagnostics;
using CupriCut.Services;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// The ffmpeg half of reading footage: a real encoded file becomes frames the analysis can read.
///
/// <para>The clip is BUILT here rather than checked in, so the test states where the cuts are
/// instead of asserting that a binary did not change. Three flat colours of one second each means
/// cuts at exactly 1.000s and 2.000s, through a real encode, decode, downscale and greyscale
/// conversion.</para>
/// </summary>
public class VideoDecodeTests(ITestOutputHelper output)
{
    private static string Ffmpeg => new CupriCut.Configuration.CutOptions().FfmpegPath;

    [Fact]
    public void The_cuts_in_a_real_file_land_where_they_were_encoded()
    {
        using var harness = new Harness();
        var path = ThreeShots(harness, "cuts.mp4");

        var analysis = VideoCues.Analyse(Ffmpeg, path, fps: 30);

        output.WriteLine($"{analysis.Seconds}s, {analysis.Motion.Count} frames");
        output.WriteLine(string.Join("\n", analysis.Cues.Select(c => c.Describe())));

        var cuts = analysis.Of(CueKind.SceneChange).ToList();

        Assert.Equal(2, cuts.Count);
        Assert.Equal(1.0, cuts[0].At, 1);
        Assert.Equal(2.0, cuts[1].At, 1);
        Assert.Equal("cuts.mp4", analysis.Source);
    }

    [Fact]
    public void The_footage_is_sampled_at_the_rate_that_was_asked_for()
    {
        // The cue frame numbers are only meaningful at the rate they were read at, so the sampling
        // rate is the caller's to choose rather than the file's to dictate.
        using var harness = new Harness();
        var path = ThreeShots(harness, "cuts.mp4");

        var at30 = VideoCues.Analyse(Ffmpeg, path, fps: 30);
        var at10 = VideoCues.Analyse(Ffmpeg, path, fps: 10);

        Assert.Equal(90, at30.Motion.Count);
        Assert.Equal(30, at10.Motion.Count);

        // Same moment, different frame number, which is the whole point of carrying both.
        Assert.Equal(at30.Of(CueKind.SceneChange).First().At, at10.Of(CueKind.SceneChange).First().At, 1);
        Assert.Equal(30, at30.Of(CueKind.SceneChange).First().Frame);
        Assert.Equal(10, at10.Of(CueKind.SceneChange).First().Frame);
    }

    [Fact]
    public void Luminance_follows_the_actual_brightness_of_each_shot()
    {
        using var harness = new Harness();
        var path = ThreeShots(harness, "cuts.mp4");

        var analysis = VideoCues.Analyse(Ffmpeg, path, fps: 30);

        // Black, then mid grey, then white - so the three shots must be in ascending order.
        double first = analysis.Luminance[15], second = analysis.Luminance[45], third = analysis.Luminance[75];
        output.WriteLine($"shot luminance: {first}, {second}, {third}");

        Assert.True(first < second, $"{first} should be darker than {second}");
        Assert.True(second < third, $"{second} should be darker than {third}");
    }

    [Fact]
    public void A_ceiling_stops_a_mistaken_argument_reading_a_feature_film()
    {
        using var harness = new Harness();
        var path = ThreeShots(harness, "cuts.mp4");

        var analysis = VideoCues.Analyse(Ffmpeg, path, fps: 30, maxSeconds: 1);

        Assert.InRange(analysis.Motion.Count, 28, 32);
    }

    [Fact]
    public void A_file_that_is_not_there_says_so_rather_than_launching_ffmpeg()
    {
        using var harness = new Harness();

        var ex = Assert.Throws<CutPolicyException>(
            () => VideoCues.Analyse(Ffmpeg, Path.Combine(harness.Root, "nope.mp4"), fps: 30));

        Assert.Contains("No video file", ex.Message);
    }

    [Fact]
    public void A_file_that_is_not_video_fails_with_what_ffmpeg_said()
    {
        using var harness = new Harness();
        var path = Path.Combine(harness.Compositions, "notvideo.mp4");
        File.WriteAllText(path, "this is not an mp4");

        var ex = Assert.Throws<CutPolicyException>(() => VideoCues.Analyse(Ffmpeg, path, fps: 30));

        output.WriteLine(ex.Message);
        Assert.Contains("notvideo.mp4", ex.Message);
    }

    [Fact]
    public void The_tool_answers_where_to_put_a_caption()
    {
        using var harness = new Harness();
        ThreeShots(harness, "cuts.mp4");

        var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            CupriCut.Tools.AudioTools.AnalyseVideo(harness.Cut, "cuts.mp4", fps: 30, calmWindow: 0.5));

        output.WriteLine(json.ToString());

        Assert.Equal(2, json.GetProperty("cuts").GetArrayLength());

        var calm = json.GetProperty("calm");
        Assert.True(calm.GetProperty("found").GetBoolean());

        // The window it picked must not contain either cut, at 1.0s and 2.0s.
        var at = calm.GetProperty("at").GetDouble();
        Assert.True(at + 0.5 <= 1.0 || (at >= 1.0 && at + 0.5 <= 2.0) || at >= 2.0,
            $"a 0.5s window at {at}s crosses a cut");
    }

    [Fact]
    public void The_tool_says_so_when_the_window_is_longer_than_the_footage()
    {
        using var harness = new Harness();
        ThreeShots(harness, "cuts.mp4");

        var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
            CupriCut.Tools.AudioTools.AnalyseVideo(harness.Cut, "cuts.mp4", fps: 30, calmWindow: 30));

        var calm = json.GetProperty("calm");

        Assert.False(calm.GetProperty("found").GetBoolean());
        Assert.Contains("shorter", calm.GetProperty("note").GetString());
    }

    /// <summary>Three flat shots of one second each: black, mid grey, white. Cuts at 1s and 2s.</summary>
    private static string ThreeShots(Harness harness, string name)
    {
        var path = Path.Combine(harness.Compositions, name);

        var args = string.Join(' ',
            "-v", "error", "-y",
            "-f", "lavfi", "-i", "color=c=black:s=320x180:d=1:r=30",
            "-f", "lavfi", "-i", "color=c=gray:s=320x180:d=1:r=30",
            "-f", "lavfi", "-i", "color=c=white:s=320x180:d=1:r=30",
            "-filter_complex", "[0:v][1:v][2:v]concat=n=3:v=1:a=0[v]",
            "-map", "[v]",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-r", "30",
            $"\"{path}\"");

        using var process = Process.Start(new ProcessStartInfo(Ffmpeg, args)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0 && File.Exists(path), $"could not build the clip: {stderr}");
        return path;
    }
}
