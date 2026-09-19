using CupriCut.Services;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// The analysis, against signals built to order.
///
/// <para><b>Why synthetic and not a recording.</b> A test over a real track can only assert that
/// the answer did not change, which catches a regression and proves nothing about whether the
/// answer was ever right. A click at exactly 2.000s either comes back at 2.000s or does not.</para>
///
/// <para>The honest limit of that is stated where it bites: these signals are percussive and
/// noiseless, which is what this method is best at. The tempo tests deliberately include a case it
/// should REFUSE, because a confident wrong answer is the failure mode that matters.</para>
/// </summary>
public class AudioCueTests(ITestOutputHelper output)
{
    private const int Rate = 22050;

    [Fact]
    public void A_click_is_found_where_it_was_put()
    {
        var samples = Silence(seconds: 3);
        Click(samples, at: 1.0);
        Click(samples, at: 2.0);

        var onsets = AudioCues.Analyse(samples, Rate, fps: 30).Of(CueKind.Onset).ToList();

        Assert.Equal(2, onsets.Count);
        Assert.Equal(1.0, onsets[0].At, 1);
        Assert.Equal(2.0, onsets[1].At, 1);
    }

    [Fact]
    public void A_cue_lands_on_a_frame_and_says_how_far_it_moved()
    {
        // A frame is the smallest thing a render has, so a cue between two of them cannot be acted
        // on. The distance it travelled is the author's business, not something to absorb.
        var samples = Silence(seconds: 2);
        Click(samples, at: 1.0);

        var cue = AudioCues.Analyse(samples, Rate, fps: 25).Of(CueKind.Onset).Single();

        Assert.Equal(cue.Frame / 25.0, cue.At, 6);
        Assert.True(Math.Abs(cue.SnapMs) <= 1000.0 / 25 / 2 + 0.001,
            $"snapped {cue.SnapMs}ms, further than half a frame");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(25)]
    [InlineData(60)]
    public void The_same_click_snaps_to_whatever_rate_was_asked_for(double fps)
    {
        var samples = Silence(seconds: 2);
        Click(samples, at: 1.0);

        var cue = AudioCues.Analyse(samples, Rate, fps).Of(CueKind.Onset).Single();

        Assert.Equal((int)Math.Round(cue.At * fps), cue.Frame);
        Assert.Equal(1.0, cue.At, 1);
    }

    [Fact]
    public void Two_hits_too_close_together_are_one_hit()
    {
        // 30ms apart. Below the 60ms floor, this is one drum being counted twice.
        var samples = Silence(seconds: 2);
        Click(samples, at: 1.0);
        Click(samples, at: 1.03);

        Assert.Single(AudioCues.Analyse(samples, Rate, fps: 30).Of(CueKind.Onset));
    }

    [Fact]
    public void A_quiet_hit_in_a_quiet_passage_is_still_found()
    {
        // The whole reason the threshold is local. A fixed one finds every hit in the loud section
        // and none in the quiet one, which is the opposite of useful.
        var samples = Silence(seconds: 6);
        for (var t = 0.5; t < 2.5; t += 0.5) Click(samples, at: t, amplitude: 0.9f);
        for (var t = 4.0; t < 5.5; t += 0.5) Click(samples, at: t, amplitude: 0.05f);

        var onsets = AudioCues.Analyse(samples, Rate, fps: 30).Of(CueKind.Onset).ToList();

        output.WriteLine(string.Join("\n", onsets.Select(c => c.Describe())));
        Assert.Contains(onsets, c => c.At > 3.9 && c.At < 5.6);
    }

    [Fact]
    public void Strength_is_relative_to_the_loudest_thing_in_the_track()
    {
        var samples = Silence(seconds: 4);
        Click(samples, at: 1.0, amplitude: 1.0f);
        Click(samples, at: 2.0, amplitude: 0.25f);

        var onsets = AudioCues.Analyse(samples, Rate, fps: 30).Of(CueKind.Onset).ToList();

        Assert.Equal(2, onsets.Count);
        Assert.True(onsets[0].Strength > onsets[1].Strength,
            $"the loud hit read {onsets[0].Strength} and the quiet one {onsets[1].Strength}");
        Assert.InRange(onsets[0].Strength, 0, 1);
    }

    // ---- tempo -------------------------------------------------------------------------------

    [Theory]
    [InlineData(120)]
    [InlineData(90)]
    [InlineData(140)]
    public void A_steady_click_track_reports_its_own_tempo(double bpm)
    {
        var samples = ClickTrack(bpm, seconds: 12);

        var tempo = AudioCues.Analyse(samples, Rate, fps: 30).Tempo;

        output.WriteLine($"asked {bpm}, got {tempo.Bpm} at confidence {tempo.Confidence}");

        // Within half a percent. The first version of this test allowed four, which passed while
        // the grid was 0.63% out - and 0.63% is 76ms of drift across twelve seconds, more than two
        // frames, with the far end of the grid sitting on nothing. A tolerance loose enough to
        // pass a broken answer is not a test.
        Assert.True(Math.Abs(tempo.Bpm - bpm) < bpm * 0.005,
            $"expected {bpm} BPM to within 0.5%, got {tempo.Bpm}");
        Assert.True(tempo.Usable, $"confidence {tempo.Confidence} was below the floor");
    }

    [Fact]
    public void Something_with_no_pulse_is_refused_rather_than_guessed()
    {
        // The failure mode that matters. Autocorrelation will always return SOME lag; the question
        // is whether the caller is told it means nothing. Random hits at irregular spacings.
        var samples = Silence(seconds: 12);
        var random = new Random(7);
        var t = 0.3;
        while (t < 11.5)
        {
            Click(samples, at: t, amplitude: 0.4f + (float)random.NextDouble() * 0.5f);
            t += 0.15 + random.NextDouble() * 0.9;
        }

        var analysis = AudioCues.Analyse(samples, Rate, fps: 30);

        output.WriteLine($"bpm {analysis.Tempo.Bpm}, confidence {analysis.Tempo.Confidence}");
        Assert.False(analysis.Tempo.Usable,
            $"claimed {analysis.Tempo.Bpm} BPM at confidence {analysis.Tempo.Confidence} on noise");
        Assert.Empty(analysis.Of(CueKind.Beat));
        Assert.Empty(analysis.Of(CueKind.Downbeat));
    }

    [Fact]
    public void Beats_are_laid_across_the_whole_track_with_every_fourth_a_downbeat()
    {
        var samples = ClickTrack(120, seconds: 8);

        var analysis = AudioCues.Analyse(samples, Rate, fps: 30);
        var grid = analysis.Cues.Where(c => c.Kind is CueKind.Beat or CueKind.Downbeat).ToList();

        // 120 BPM over 8s is about 16 beats, and a quarter of them are downbeats.
        Assert.InRange(grid.Count, 14, 17);
        Assert.InRange(analysis.Count(CueKind.Downbeat), 3, 5);

        var spacing = grid[1].At - grid[0].At;
        Assert.Equal(0.5, spacing, 1);
    }

    [Fact]
    public void A_beat_nobody_played_reads_weak_rather_than_missing()
    {
        // The grid is a grid. If the drummer drops beat three, the cue is still there, carrying the
        // energy actually found - which is information, and better than a hole.
        var samples = Silence(seconds: 10);
        var index = 0;
        for (var t = 0.5; t < 9.5; t += 0.5, index++)
            if (index % 8 != 5) Click(samples, at: t);

        var analysis = AudioCues.Analyse(samples, Rate, fps: 30);

        // Asserted rather than skipped. The first version returned early when no grid was found,
        // which would have let the interesting half of this test quietly never run.
        Assert.True(analysis.Tempo.Usable,
            $"no usable grid ({analysis.Tempo.Bpm} BPM at {analysis.Tempo.Confidence}), so the rest proves nothing");

        var grid = analysis.Cues.Where(c => c.Kind is CueKind.Beat or CueKind.Downbeat).ToList();

        Assert.Contains(grid, c => c.Strength < 0.1);
        Assert.Contains(grid, c => c.Strength > 0.5);
    }

    // ---- silence -----------------------------------------------------------------------------

    [Fact]
    public void Where_sound_starts_and_stops_is_reported()
    {
        // What a lower third actually keys off: two seconds of quiet, three of tone, two of quiet.
        var samples = Silence(seconds: 7);
        Tone(samples, from: 2.0, to: 5.0);

        var analysis = AudioCues.Analyse(samples, Rate, fps: 30);

        var start = Assert.Single(analysis.Of(CueKind.SoundStart));
        var end = Assert.Single(analysis.Of(CueKind.SoundEnd));

        Assert.Equal(2.0, start.At, 1);
        Assert.Equal(5.0, end.At, 1);
    }

    [Fact]
    public void A_gap_between_two_words_is_not_the_end_of_the_speech()
    {
        // 80ms of quiet inside a phrase. Under the 250ms floor, so it is not a boundary.
        var samples = Silence(seconds: 6);
        Tone(samples, from: 1.0, to: 3.0);
        Tone(samples, from: 3.08, to: 5.0);

        var analysis = AudioCues.Analyse(samples, Rate, fps: 30);

        Assert.Single(analysis.Of(CueKind.SoundStart));
        Assert.Single(analysis.Of(CueKind.SoundEnd));
    }

    // ---- envelope ----------------------------------------------------------------------------

    [Fact]
    public void The_loudness_envelope_is_one_value_per_frame()
    {
        // Per frame, not per hop: the consumer is a renderer, and an envelope at the analysis rate
        // would have to be resampled by every caller, differently.
        var samples = Silence(seconds: 4);
        Tone(samples, from: 1.0, to: 3.0);

        var analysis = AudioCues.Analyse(samples, Rate, fps: 30);

        Assert.Equal(120, analysis.Loudness.Count);
        Assert.All(analysis.Loudness, v => Assert.InRange(v, 0, 1));
        Assert.True(analysis.Loudness[60] > analysis.Loudness[5],
            "the middle of the tone should be louder than the silence at the start");
    }

    [Fact]
    public void Silence_analyses_without_inventing_anything()
    {
        var analysis = AudioCues.Analyse(Silence(seconds: 3), Rate, fps: 30);

        Assert.Empty(analysis.Of(CueKind.Onset));
        Assert.False(analysis.Tempo.Usable);
    }

    [Fact]
    public void A_signal_shorter_than_one_window_does_not_throw()
    {
        var analysis = AudioCues.Analyse(new float[100], Rate, fps: 30);

        Assert.Empty(analysis.Cues);
        Assert.Empty(analysis.Loudness);
    }

    // ---- signals -----------------------------------------------------------------------------

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    /// <summary>A short burst of decaying noise - a drum hit, near enough, and broadband so it
    /// shows up across the spectrum the way a real one does.</summary>
    private static void Click(float[] samples, double at, float amplitude = 0.8f)
    {
        var start = (int)(at * Rate);
        var length = Rate / 50;                 // 20ms
        var random = new Random(start);         // deterministic per position

        for (var i = 0; i < length && start + i < samples.Length; i++)
        {
            var decay = 1 - (float)i / length;
            samples[start + i] += amplitude * decay * decay * ((float)random.NextDouble() * 2 - 1);
        }
    }

    private static float[] ClickTrack(double bpm, double seconds)
    {
        var samples = Silence(seconds);
        var period = 60 / bpm;
        for (var t = 0.25; t < seconds - 0.1; t += period) Click(samples, t);
        return samples;
    }

    /// <summary>A steady 220 Hz tone, with 10ms ramps so its own edges are not onsets.</summary>
    private static void Tone(float[] samples, double from, double to)
    {
        int start = (int)(from * Rate), end = Math.Min((int)(to * Rate), samples.Length);
        var ramp = Rate / 100;

        for (var i = start; i < end; i++)
        {
            var fade = Math.Min(Math.Min(i - start, end - i) / (double)ramp, 1);
            samples[i] += (float)(0.5 * fade * Math.Sin(2 * Math.PI * 220 * (i - start) / Rate));
        }
    }
}
