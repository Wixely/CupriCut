using System.Text.Json;
using CupriCut.Services;
using CupriCut.Tools;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// Cues stored in the project rather than re-read on every render.
///
/// <para>Two reasons, and both are the same reason. A render has to be reproducible on a machine
/// with a different ffmpeg, and audio analysis is exactly the sort of thing that drifts between
/// versions - a cue that moves by a frame between two machines is the class of bug this project
/// exists to avoid. And an agent should not pay for the analysis on every iteration of a
/// look-adjust-look loop when nothing about the track has changed.</para>
///
/// <para>Which makes the hash the interesting part: storing an answer is only safe if replacing
/// the question is noticed.</para>
/// </summary>
public class ProjectAudioTests(ITestOutputHelper output)
{
    [Fact]
    public void Attaching_a_track_stores_its_cues_and_they_survive_a_reload()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject { Html = Harness.Keyframed });
        Wav(harness, "beat.wav", bpm: 120, seconds: 6);

        var answer = JsonSerializer.Deserialize<JsonElement>(
            AudioTools.AttachAudio(harness.Cut, "hero", "beat.wav", fps: 30));

        output.WriteLine(answer.ToString());

        var reloaded = harness.Cut.LoadProject("hero");
        var audio = reloaded.Audio!;

        Assert.Equal("beat.wav", audio.Source);
        Assert.Equal(30, audio.Fps);
        Assert.NotEmpty(audio.Sha256);
        Assert.NotEmpty(audio.Cues);
        Assert.Contains(audio.Cues, c => c.Kind == CueKind.Onset);
    }

    [Fact]
    public void The_cues_are_the_same_ones_the_analysis_gave()
    {
        // Stored, not re-derived: what comes back out has to be what went in, or the whole point
        // of storing it is gone.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject { Html = Harness.Keyframed });
        var path = Wav(harness, "beat.wav", bpm: 120, seconds: 6);

        var direct = AudioDecoder.Analyse(new CupriCut.Configuration.CutOptions().FfmpegPath, path, 30);
        AudioTools.AttachAudio(harness.Cut, "hero", "beat.wav", fps: 30);

        var stored = harness.Cut.LoadProject("hero").Audio!;

        Assert.Equal(direct.Cues.Count, stored.Cues.Count);
        Assert.Equal(direct.Cues.Select(c => c.At), stored.Cues.Select(c => c.At));
        Assert.Equal(direct.Cues.Select(c => c.Frame), stored.Cues.Select(c => c.Frame));
        Assert.Equal(direct.Tempo.Bpm, stored.Bpm);
    }

    [Fact]
    public void The_track_itself_travels_with_the_project()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero" + CutPackage.Extension, new CutProject { Html = Harness.Keyframed });
        Wav(harness, "beat.wav", bpm: 120, seconds: 4);

        AudioTools.AttachAudio(harness.Cut, "hero" + CutPackage.Extension, "beat.wav", fps: 30);

        var reloaded = harness.Cut.LoadProject("hero" + CutPackage.Extension);

        Assert.Equal("beat.wav", reloaded.Audio!.Asset);
        Assert.True(reloaded.Assets.ContainsKey("beat.wav"));
        Assert.StartsWith("data:audio/wav;base64,", reloaded.Assets["beat.wav"]);
    }

    [Fact]
    public void A_package_stores_the_track_as_bytes_rather_than_base64()
    {
        // The reason the container exists, on the case it was built for.
        using var harness = new Harness();
        Wav(harness, "beat.wav", bpm: 120, seconds: 6);

        harness.Cut.SaveProject("json", new CutProject { Html = Harness.Keyframed });
        harness.Cut.SaveProject("packed" + CutPackage.Extension, new CutProject { Html = Harness.Keyframed });

        AudioTools.AttachAudio(harness.Cut, "json", "beat.wav", fps: 30);
        AudioTools.AttachAudio(harness.Cut, "packed" + CutPackage.Extension, "beat.wav", fps: 30);

        var projects = Path.Combine(harness.Root, "projects");
        long json = new FileInfo(Path.Combine(projects, "json.cut.json")).Length;
        long packed = new FileInfo(Path.Combine(projects, "packed" + CutPackage.Extension)).Length;

        output.WriteLine($"json {json:N0} bytes, package {packed:N0} bytes");
        Assert.True(packed < json * 0.8, $"the package should be well under the JSON; was {packed:N0} against {json:N0}");
    }

    [Fact]
    public void Cues_can_be_kept_without_the_track()
    {
        // Enough to animate, not enough to mux. A legitimate choice, so it is not warned about.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject { Html = Harness.Keyframed });
        Wav(harness, "beat.wav", bpm: 120, seconds: 4);

        AudioTools.AttachAudio(harness.Cut, "hero", "beat.wav", fps: 30, embed: false);

        var reloaded = harness.Cut.LoadProject("hero");

        Assert.Null(reloaded.Audio!.Asset);
        Assert.Empty(reloaded.Assets);
        Assert.NotEmpty(reloaded.Audio.Cues);
        Assert.Equal("clean", Inspector.Examine(harness.Cut, "hero.cut.json").Verdict);
    }

    [Fact]
    public void Swapping_the_track_under_the_cues_is_caught()
    {
        // The point of keeping a hash. Without this the animation is simply wrong, and stays wrong
        // until somebody watches the whole thing and notices nothing lands.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject { Html = Harness.Keyframed });
        Wav(harness, "beat.wav", bpm: 120, seconds: 4);
        AudioTools.AttachAudio(harness.Cut, "hero", "beat.wav", fps: 30);

        // A different track, put in place of the one the cues were read from.
        var other = Wav(harness, "other.wav", bpm: 90, seconds: 4);
        var project = harness.Cut.LoadProject("hero");
        project.Assets["beat.wav"] = "data:audio/wav;base64," + Convert.ToBase64String(File.ReadAllBytes(other));
        harness.Cut.SaveProject("hero", project);

        var examination = Inspector.Examine(harness.Cut, "hero.cut.json");

        var found = Assert.Single(examination.Findings, f => f.Code == Inspector.StaleAudio);
        output.WriteLine(found.What);
        Assert.Contains("replaced", found.What);
        Assert.Equal("warnings", examination.Verdict);
    }

    [Fact]
    public void A_project_whose_track_is_unchanged_is_clean()
    {
        using var harness = new Harness();
        harness.Cut.SaveProject("hero" + CutPackage.Extension, new CutProject { Html = Harness.Keyframed });
        Wav(harness, "beat.wav", bpm: 120, seconds: 4);
        AudioTools.AttachAudio(harness.Cut, "hero" + CutPackage.Extension, "beat.wav", fps: 30);

        var examination = Inspector.Examine(harness.Cut, "hero" + CutPackage.Extension);

        Assert.DoesNotContain(examination.Findings, f => f.Code == Inspector.StaleAudio);
    }

    [Fact]
    public void The_rate_comes_from_the_project_when_the_caller_names_none()
    {
        // A cue is only actionable at the rate it was snapped to, so it should default to the rate
        // the project actually renders at rather than to a global one.
        using var harness = new Harness();
        harness.Cut.SaveProject("hero", new CutProject
        {
            Html = Harness.Keyframed,
            Render = new RenderSettings { Fps = 25 },
        });
        Wav(harness, "beat.wav", bpm: 120, seconds: 4);

        AudioTools.AttachAudio(harness.Cut, "hero", "beat.wav");

        var audio = harness.Cut.LoadProject("hero").Audio!;

        Assert.Equal(25, audio.Fps);
        Assert.All(audio.Cues, c => Assert.Equal(c.Frame / 25.0, c.At, 6));
    }

    /// <summary>A click track on disk, as a 16-bit mono WAV.</summary>
    private static string Wav(Harness harness, string name, double bpm, double seconds)
    {
        const int Rate = 22050;
        var frames = (int)(seconds * Rate);
        var samples = new short[frames];

        var bed = new Random(1);
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)bed.Next(-50, 50);

        for (var t = 0.25; t < seconds - 0.1; t += 60 / bpm)
        {
            var start = (int)(t * Rate);
            var length = Rate / 50;
            var noise = new Random(start);

            for (var i = 0; i < length && start + i < frames; i++)
            {
                var decay = 1 - (double)i / length;
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
