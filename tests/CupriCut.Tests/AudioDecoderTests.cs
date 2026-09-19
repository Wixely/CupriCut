using CupriCut.Services;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// The ffmpeg half: a file on disk becomes samples the analysis can read.
///
/// <para>A WAV is written here rather than checked in, so the test states the signal it expects to
/// get back instead of asserting that a binary did not change. A click at 2.000s in the file has to
/// come back as a cue at 2.000s after a decode, a downmix and a resample.</para>
/// </summary>
public class AudioDecoderTests(ITestOutputHelper output)
{
    [Fact]
    public void A_file_decodes_to_the_rate_and_channel_count_the_analysis_wants()
    {
        // Written as 44100 Hz STEREO, deliberately: the decoder's job includes downmixing and
        // resampling, and a test at the target rate would prove neither.
        using var harness = new Harness();
        var path = WriteWav(harness, "tone.wav", seconds: 2, rate: 44100, stereo: true, click: []);

        var decoded = AudioDecoder.Decode(Ffmpeg, path);

        Assert.Equal(AudioDecoder.Rate, decoded.SampleRate);
        Assert.Equal(2.0, decoded.Seconds, 1);
    }

    [Fact]
    public void A_click_in_the_file_comes_back_as_a_cue_at_the_same_moment()
    {
        using var harness = new Harness();
        var path = WriteWav(harness, "clicks.wav", seconds: 4, rate: 44100, stereo: false,
            click: [1.0, 2.0, 3.0]);

        var analysis = AudioDecoder.Analyse(Ffmpeg, path, fps: 30);

        var onsets = analysis.Of(CueKind.Onset).ToList();
        output.WriteLine(string.Join("\n", onsets.Select(c => c.Describe())));

        Assert.Equal(3, onsets.Count);
        Assert.Equal(1.0, onsets[0].At, 1);
        Assert.Equal(2.0, onsets[1].At, 1);
        Assert.Equal(3.0, onsets[2].At, 1);
        Assert.Equal("clicks.wav", analysis.Source);
    }

    [Fact]
    public void The_hash_is_of_the_file_so_swapping_the_track_is_catchable()
    {
        using var harness = new Harness();
        var one = WriteWav(harness, "a.wav", seconds: 1, rate: 22050, stereo: false, click: [0.5]);
        var other = WriteWav(harness, "b.wav", seconds: 1, rate: 22050, stereo: false, click: [0.7]);

        Assert.Equal(AudioDecoder.Hash(one), AudioDecoder.Hash(one));
        Assert.NotEqual(AudioDecoder.Hash(one), AudioDecoder.Hash(other));
    }

    [Fact]
    public void A_ceiling_stops_a_mistaken_argument_reading_an_hour_into_memory()
    {
        using var harness = new Harness();
        var path = WriteWav(harness, "long.wav", seconds: 6, rate: 22050, stereo: false, click: []);

        var decoded = AudioDecoder.Decode(Ffmpeg, path, maxSeconds: 2);

        Assert.Equal(2.0, decoded.Seconds, 1);
    }

    [Fact]
    public void A_file_that_is_not_there_says_so_rather_than_launching_ffmpeg()
    {
        using var harness = new Harness();

        var ex = Assert.Throws<CutPolicyException>(
            () => AudioDecoder.Decode(Ffmpeg, Path.Combine(harness.Root, "nope.wav")));

        Assert.Contains("No audio file", ex.Message);
    }

    [Fact]
    public void A_file_that_is_not_audio_fails_with_what_ffmpeg_said()
    {
        using var harness = new Harness();
        var path = Path.Combine(harness.Compositions, "notaudio.wav");
        File.WriteAllText(path, "this is not a wave file");

        var ex = Assert.Throws<CutPolicyException>(() => AudioDecoder.Decode(Ffmpeg, path));

        output.WriteLine(ex.Message);
        Assert.Contains("notaudio.wav", ex.Message);
    }

    // ---- a WAV, written by hand ---------------------------------------------------------------

    private static string Ffmpeg => new CupriCut.Configuration.CutOptions().FfmpegPath;

    /// <summary>16-bit PCM, because that is what everything reads and it is 40 lines.</summary>
    private static string WriteWav(
        Harness harness, string name, double seconds, int rate, bool stereo, double[] click)
    {
        var channels = stereo ? 2 : 1;
        var frames = (int)(seconds * rate);
        var samples = new short[frames * channels];

        // A quiet bed, so the file is not digital silence - which some decoders treat specially.
        var bed = new Random(1);
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(bed.Next(-60, 60));

        foreach (var at in click)
        {
            var start = (int)(at * rate);
            var length = rate / 50;
            var noise = new Random(start);

            for (var i = 0; i < length && start + i < frames; i++)
            {
                var decay = 1 - (double)i / length;
                var value = (short)(26000 * decay * decay * (noise.NextDouble() * 2 - 1));
                for (var c = 0; c < channels; c++) samples[(start + i) * channels + c] = value;
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
        w.Write(16);                                   // PCM header size
        w.Write((short)1);                             // PCM
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);                  // byte rate
        w.Write((short)(channels * 2));                // block align
        w.Write((short)16);                            // bits
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);

        return path;
    }
}
