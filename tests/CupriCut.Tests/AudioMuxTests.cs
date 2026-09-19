using System.Text.Json;
using CupriCut.Services;
using CupriCut.Tools;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// Muxing the track into the finished file.
///
/// <para>Asserted against the FILE rather than against the arguments, because the interesting
/// failures all happen inside ffmpeg: a container that will not take the codec it was handed either
/// refuses loudly or drops the stream and leaves a silent file that looks exactly like a correct
/// one. Asking ffprobe what actually came out is the only version of this test worth having - the
/// same reason alpha has been verified rather than assumed since the beginning.</para>
/// </summary>
public class AudioMuxTests(ITestOutputHelper output)
{
    [Fact]
    public void A_projects_track_ends_up_in_the_video()
    {
        using var harness = new Harness();
        Prepare(harness, "hero" + CutPackage.Extension);

        var json = JsonSerializer.Deserialize<JsonElement>(RenderTools.RenderVideo(
            harness.Cut, harness.Encoder, "hero" + CutPackage.Extension, duration: 1, fps: 15));

        var path = json.GetProperty("video").GetString()!;
        output.WriteLine(json.ToString());

        Assert.Equal("aac", harness.Encoder.AudioCodecOf(path));
        Assert.True(json.GetProperty("audio").GetProperty("muxed").GetBoolean());
    }

    [Fact]
    public void No_audio_renders_silent_even_when_the_project_carries_a_track()
    {
        using var harness = new Harness();
        Prepare(harness, "hero" + CutPackage.Extension);

        var json = JsonSerializer.Deserialize<JsonElement>(RenderTools.RenderVideo(
            harness.Cut, harness.Encoder, "hero" + CutPackage.Extension, duration: 1, fps: 15, noAudio: true));

        Assert.Null(harness.Encoder.AudioCodecOf(json.GetProperty("video").GetString()!));
        Assert.False(json.TryGetProperty("audio", out var a) && a.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public void A_composition_with_no_track_is_silent_and_says_nothing_about_audio()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("plain.html", Harness.Keyframed);

        var json = JsonSerializer.Deserialize<JsonElement>(
            RenderTools.RenderVideo(harness.Cut, harness.Encoder, name, duration: 1, fps: 15));

        Assert.Null(harness.Encoder.AudioCodecOf(json.GetProperty("video").GetString()!));
    }

    [Fact]
    public void The_mask_half_of_a_keying_pair_is_silent_and_the_colour_half_is_not()
    {
        // The case this feature could most easily get wrong. A mask is half of a pair whose other
        // half already carries the sound; muxing it into both doubles the bytes for nothing and
        // desynchronises whoever assembles them later.
        using var harness = new Harness();
        Prepare(harness, "hero" + CutPackage.Extension);

        var json = JsonSerializer.Deserialize<JsonElement>(RenderTools.Export(
            harness.Cut, harness.Encoder, "hero" + CutPackage.Extension, formats: ["mp4", "mask"],
            duration: 1, fps: 15, alpha: true));

        output.WriteLine(json.ToString());
        var files = json.GetProperty("files").EnumerateArray().ToList();

        var colour = files.Single(f => f.GetProperty("format").GetString() == "mp4");
        var mask = files.Single(f => f.GetProperty("format").GetString() == "mask");

        Assert.Equal("aac", harness.Encoder.AudioCodecOf(colour.GetProperty("path").GetString()!));
        Assert.Null(harness.Encoder.AudioCodecOf(mask.GetProperty("path").GetString()!));

        // And the answer says which, because a silent half is exactly where correct looks broken.
        Assert.Contains("mp4", json.GetProperty("audio").GetProperty("into").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("mask", json.GetProperty("audio").GetProperty("without").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void A_gif_gets_no_audio_because_the_container_has_none()
    {
        using var harness = new Harness();
        Prepare(harness, "hero" + CutPackage.Extension);

        var json = JsonSerializer.Deserialize<JsonElement>(RenderTools.Export(
            harness.Cut, harness.Encoder, "hero" + CutPackage.Extension, formats: ["gif"], duration: 1, fps: 15));

        var gif = json.GetProperty("files").EnumerateArray().Single();

        Assert.Null(harness.Encoder.AudioCodecOf(gif.GetProperty("path").GetString()!));
    }

    [Fact]
    public void A_named_file_wins_over_the_one_in_the_project()
    {
        using var harness = new Harness();
        Prepare(harness, "hero" + CutPackage.Extension);
        Wav(harness, "other.wav", seconds: 2);

        var json = JsonSerializer.Deserialize<JsonElement>(RenderTools.RenderVideo(
            harness.Cut, harness.Encoder, "hero" + CutPackage.Extension, duration: 1, fps: 15, audio: "other.wav"));

        Assert.Equal("other.wav", json.GetProperty("audio").GetProperty("source").GetString());
        Assert.Equal("aac", harness.Encoder.AudioCodecOf(json.GetProperty("video").GetString()!));
    }

    [Fact]
    public void A_track_named_for_a_composition_that_has_none_still_works()
    {
        // Timing to a track without storing it in the project is a perfectly ordinary thing to do
        // once, and should not require attaching anything first.
        using var harness = new Harness();
        var name = harness.WriteComposition("plain.html", Harness.Keyframed);
        Wav(harness, "beat.wav", seconds: 2);

        var json = JsonSerializer.Deserialize<JsonElement>(RenderTools.RenderVideo(
            harness.Cut, harness.Encoder, name, duration: 1, fps: 15, audio: "beat.wav"));

        Assert.Equal("aac", harness.Encoder.AudioCodecOf(json.GetProperty("video").GetString()!));
    }

    [Fact]
    public void A_track_that_is_not_there_is_refused_by_name()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("plain.html", Harness.Keyframed);

        var ex = Assert.ThrowsAny<Exception>(() => RenderTools.RenderVideo(
            harness.Cut, harness.Encoder, name, duration: 1, fps: 15, audio: "nope.wav"));

        Assert.Contains("nope.wav", ex.Message);
    }

    [Fact]
    public void Cues_without_a_track_render_silently_rather_than_failing()
    {
        // attach_audio with embed:false is a legitimate choice - enough to animate, not enough to
        // mux. A render should not then refuse to run.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 160, Height = 90, Fps = 15, Duration = 1 },
        });
        Wav(harness, "beat.wav", seconds: 2);
        AudioTools.AttachAudio(harness.Cut, "hero", "beat.wav", fps: 15, embed: false);

        var json = JsonSerializer.Deserialize<JsonElement>(
            RenderTools.RenderVideo(harness.Cut, harness.Encoder, "hero.cut.json", duration: 1, fps: 15));

        Assert.Null(harness.Encoder.AudioCodecOf(json.GetProperty("video").GetString()!));
    }

    [Fact]
    public void Webm_asks_for_opus_rather_than_aac()
    {
        // WebM accepts only Vorbis or Opus and refuses to write a header for AAC - found by
        // exporting a keying pair and watching ffmpeg fall over. Asserted on the codec table
        // rather than on a file because this ffmpeg cannot write vp9 alpha at all.
        Assert.Equal("libopus", VideoEncoder.Codecs["vp9"].AudioEncoder);
        Assert.Equal("aac", VideoEncoder.Codecs["h264"].AudioEncoder);
        Assert.Equal("aac", VideoEncoder.Codecs["prores"].AudioEncoder);
        Assert.Null(VideoEncoder.Codecs["gif"].AudioEncoder);
    }

    // ---- fixtures --------------------------------------------------------------------------

    private static void Prepare(Harness harness, string project)
    {
        harness.Cut.SaveProject(project, new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Width = 160, Height = 90, Fps = 15, Duration = 1 },
        });

        Wav(harness, "beat.wav", seconds: 2);
        AudioTools.AttachAudio(harness.Cut, project, "beat.wav", fps: 15);
    }

    /// <summary>A short 16-bit mono WAV with a few clicks in it.</summary>
    private static string Wav(Harness harness, string name, double seconds)
    {
        const int Rate = 22050;
        var frames = (int)(seconds * Rate);
        var samples = new short[frames];

        var bed = new Random(1);
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)bed.Next(-50, 50);

        for (var t = 0.25; t < seconds - 0.1; t += 0.5)
        {
            var start = (int)(t * Rate);
            var noise = new Random(start);
            for (var i = 0; i < Rate / 50 && start + i < frames; i++)
            {
                var decay = 1 - (double)i / (Rate / 50);
                samples[start + i] = (short)(26000 * decay * decay * (noise.NextDouble() * 2 - 1));
            }
        }

        var path = Path.Combine(harness.Compositions, name);
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);

        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);

        return path;
    }
}
