using System.Globalization;

namespace CupriCut.Services;

/// <summary>What a cue is a cue of.</summary>
public enum CueKind
{
    /// <summary>A hit: a sharp rise in spectral energy. Where something happens.</summary>
    Onset,

    /// <summary>A beat inferred from the tempo grid, whether or not a hit landed on it.</summary>
    Beat,

    /// <summary>The first beat of a bar, assuming four. Only emitted when the tempo is confident.</summary>
    Downbeat,

    /// <summary>Sound starts after quiet.</summary>
    SoundStart,

    /// <summary>Sound stops.</summary>
    SoundEnd,
}

/// <summary>One moment in a track worth timing something to.</summary>
/// <param name="At">When, in seconds, snapped to a frame.</param>
/// <param name="Frame">The frame it lands on at the rate the analysis was asked for.</param>
/// <param name="Kind">What sort of moment.</param>
/// <param name="Strength">How strong, 0-1, normalised within this track. For a beat, the onset
/// energy actually found at that instant - a beat the drummer left out reads weak, which is
/// information rather than an error.</param>
/// <param name="SnapMs">How far the cue moved to reach its frame, in milliseconds. Signed.</param>
public sealed record Cue(double At, int Frame, CueKind Kind, double Strength, double SnapMs)
{
    public string Describe() =>
        $"{At.ToString("0.###", CultureInfo.InvariantCulture)}s  frame {Frame,-6} {Kind,-10} "
        + $"{Strength.ToString("0.00", CultureInfo.InvariantCulture)}";
}

/// <summary>What the tempo search came to.</summary>
/// <param name="Bpm">Beats per minute, or 0 when nothing periodic was found.</param>
/// <param name="Confidence">0-1. <b>Not decoration.</b> Autocorrelation over an onset envelope is
/// confidently wrong on rubato, on half and double tempo, and on anything without a drum, so the
/// caller has to be able to tell "128 BPM" from "no usable beat here". Below
/// <see cref="AudioCues.TempoFloor"/> no beats are emitted at all.</param>
/// <param name="FirstBeat">Where the grid starts, in seconds.</param>
public sealed record Tempo(double Bpm, double Confidence, double FirstBeat)
{
    public bool Usable => Bpm > 0 && Confidence >= AudioCues.TempoFloor;
}

/// <summary>Everything one listen to a track turns up.</summary>
/// <param name="Source">What was analysed.</param>
/// <param name="Seconds">How long it is.</param>
/// <param name="SampleRate">What it was decoded at.</param>
/// <param name="Fps">The frame rate the cues are snapped to.</param>
/// <param name="Tempo">The tempo search, confidence included.</param>
/// <param name="Cues">Every cue, in time order.</param>
/// <param name="Loudness">The loudness envelope, one value per FRAME, 0-1. For anything that
/// should breathe with the track rather than snap to it.</param>
public sealed record AudioAnalysis(
    string Source,
    double Seconds,
    int SampleRate,
    double Fps,
    Tempo Tempo,
    IReadOnlyList<Cue> Cues,
    IReadOnlyList<double> Loudness)
{
    public IEnumerable<Cue> Of(CueKind kind) => Cues.Where(c => c.Kind == kind);

    public int Count(CueKind kind) => Cues.Count(c => c.Kind == kind);
}

/// <summary>
/// Where the hits are in a piece of audio.
///
/// <para><b>Why in-process rather than a library.</b> Two reasons, and the second is the real one.
/// A dependency whose version changes the answer makes a render irreproducible in exactly the way
/// this project refuses everywhere else. And the methods here are small enough to read: an FFT, a
/// spectral flux, an adaptive threshold, an autocorrelation. Something that cannot be read cannot
/// be trusted about a number as consequential as "the beat is here".</para>
///
/// <para><b>What this is honestly good at.</b> Onsets on percussive material: very good. Silence
/// boundaries: very good, and they are what a lower third actually keys off. Tempo: adequate on
/// anything with a drum and unreliable otherwise, which is why <see cref="Tempo.Confidence"/>
/// exists and why beats are withheld below a floor rather than guessed.</para>
///
/// <para>This half is pure - samples in, cues out - so it is tested against signals built to
/// order rather than against a recording, and it has no opinion about where the audio came from.
/// <see cref="AudioDecoder"/> is the half that talks to ffmpeg.</para>
/// </summary>
public static class AudioCues
{
    /// <summary>The window, in samples. 1024 at 22050 Hz is 46ms - long enough for a usable
    /// spectrum, short enough that two hits an eighth-note apart at 200 BPM stay separate.</summary>
    public const int Window = 1024;

    /// <summary>How far the window moves. Half the window, so 23ms of resolution.</summary>
    public const int Hop = 512;

    /// <summary>Below this the tempo is reported but no beats are emitted. Better to say "no
    /// usable beat" than to lay a confident grid over a piece of speech.</summary>
    public const double TempoFloor = 0.35;

    /// <summary>The slowest and fastest tempo looked for.</summary>
    public const double MinBpm = 60;

    public const double MaxBpm = 200;

    /// <summary>Listen to a signal.</summary>
    /// <param name="samples">Mono, -1 to 1.</param>
    /// <param name="sampleRate">Of those samples.</param>
    /// <param name="fps">The frame rate cues are snapped to - a cue that is not on a frame cannot
    /// be acted on, since a frame is the smallest thing a render has.</param>
    /// <param name="source">A name for the report.</param>
    public static AudioAnalysis Analyse(
        ReadOnlySpan<float> samples, int sampleRate, double fps, string source = "")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        var seconds = (double)samples.Length / sampleRate;
        var hopSeconds = (double)Hop / sampleRate;

        var (flux, rms) = Envelopes(samples);
        var tempo = FindTempo(flux, hopSeconds);

        var cues = new List<Cue>();
        cues.AddRange(Onsets(flux, hopSeconds, fps));
        cues.AddRange(Silences(rms, hopSeconds, fps));
        if (tempo.Usable) cues.AddRange(Beats(tempo, flux, hopSeconds, seconds, fps));

        return new AudioAnalysis(
            source,
            Math.Round(seconds, 6),
            sampleRate,
            fps,
            tempo,
            [.. cues.OrderBy(c => c.At).ThenBy(c => (int)c.Kind)],
            Decimate(rms, hopSeconds, seconds, fps));
    }

    // ---- envelopes ---------------------------------------------------------------------------

    /// <summary>The two envelopes the analysis is built on, one value per <see cref="Hop"/>:
    /// spectral flux (where energy RISES, which is where hits are) and RMS (how loud it is).
    ///
    /// <para>Public because it is the thing to draw. A waveform under a scrub bar is how timing by
    /// hand stops being guesswork, and a caller checking why a cue landed where it did needs to see
    /// what the detector saw.</para></summary>
    public static (double[] Flux, double[] Rms) Envelope(ReadOnlySpan<float> samples) => Envelopes(samples);

    /// <summary>Spectral flux and RMS, one value per hop.
    ///
    /// <para>Flux is the sum of POSITIVE changes in the magnitude spectrum between consecutive
    /// windows. Positive-only matters: an energy drop is not an onset, and counting it makes every
    /// note-off look like a note-on.</para></summary>
    private static (double[] Flux, double[] Rms) Envelopes(ReadOnlySpan<float> samples)
    {
        var hops = samples.Length < Window ? 0 : (samples.Length - Window) / Hop + 1;
        if (hops <= 0) return ([], []);

        var flux = new double[hops];
        var rms = new double[hops];

        var window = Hann(Window);
        var previous = new double[Window / 2];
        var re = new double[Window];
        var im = new double[Window];

        for (var h = 0; h < hops; h++)
        {
            var start = h * Hop;
            double sum = 0;

            for (var i = 0; i < Window; i++)
            {
                var s = samples[start + i];
                sum += s * s;
                re[i] = s * window[i];
                im[i] = 0;
            }

            rms[h] = Math.Sqrt(sum / Window);
            Fft(re, im);

            double rise = 0;
            for (var k = 0; k < Window / 2; k++)
            {
                var magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                var change = magnitude - previous[k];
                if (change > 0) rise += change;
                previous[k] = magnitude;
            }

            flux[h] = rise;
        }

        return (flux, rms);
    }

    private static double[] Hann(int n)
    {
        var w = new double[n];
        for (var i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    /// <summary>In-place radix-2 Cooley-Tukey. <paramref name="re"/> length must be a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wr = Math.Cos(angle);
            var wi = Math.Sin(angle);

            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var ur = re[i + k];
                    var ui = im[i + k];
                    var vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    var vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;

                    re[i + k] = ur + vr;
                    im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr;
                    im[i + k + len / 2] = ui - vi;

                    var nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
    }

    // ---- onsets -----------------------------------------------------------------------------

    /// <summary>A flux peak that stands above its own neighbourhood, as a hop index.
    ///
    /// <para>The threshold is LOCAL - a median over a moving window plus a margin - rather than one
    /// number for the whole track. A fixed threshold finds every hit in the loud section and none
    /// in the quiet one, which is the opposite of what an author wants.</para>
    ///
    /// <para>Separate from <see cref="Onsets"/> because the tempo search needs the same peaks: a
    /// grid fitted to different hits than the ones reported would drift away from its own cues.</para></summary>
    private static List<int> Peaks(double[] flux, double hopSeconds)
    {
        var found = new List<int>();
        if (flux.Length == 0) return found;

        const int Neighbourhood = 12;   // ~0.28s either side
        var peak = flux.Max();
        if (peak <= 0) return found;

        // No two onsets closer than 60ms: below that it is one hit being counted twice.
        var minimumGap = (int)Math.Ceiling(0.06 / hopSeconds);
        var last = -minimumGap - 1;

        for (var h = 1; h < flux.Length - 1; h++)
        {
            if (flux[h] < flux[h - 1] || flux[h] < flux[h + 1]) continue;

            var from = Math.Max(0, h - Neighbourhood);
            var to = Math.Min(flux.Length, h + Neighbourhood + 1);

            if (flux[h] < Median(flux, from, to) * 1.6 + peak * 0.06) continue;
            if (h - last < minimumGap) continue;

            last = h;
            found.Add(h);
        }

        return found;
    }

    private static IEnumerable<Cue> Onsets(double[] flux, double hopSeconds, double fps)
    {
        var peak = flux.Length > 0 ? flux.Max() : 0;
        if (peak <= 0) yield break;

        foreach (var h in Peaks(flux, hopSeconds))
            yield return Snap(h * hopSeconds, CueKind.Onset, flux[h] / peak, fps);
    }

    private static double Median(double[] values, int from, int to)
    {
        var slice = values[from..to];
        Array.Sort(slice);
        return slice.Length == 0 ? 0 : slice[slice.Length / 2];
    }

    // ---- tempo ------------------------------------------------------------------------------

    /// <summary>Autocorrelation of the onset envelope, then three corrections - each of which
    /// matters more than the autocorrelation does.
    ///
    /// <para><b>A lag is not a period.</b> 120 BPM at this hop size is 21.53 hops, and there is no
    /// such lag. Split between 21 and 22, neither correlates as well as 43 does, so the raw winner
    /// was 60.09 BPM - exactly half. The peak is interpolated to fractional precision before
    /// anything else looks at it.</para>
    ///
    /// <para><b>The octave error.</b> Every multiple of the true period is a real peak, so a
    /// half-speed answer is always available and often wins. The candidates are therefore the
    /// refined period and its halves and thirds, judged on agreement rather than correlation: a
    /// beat is the FASTEST period that explains the track, not any period that does.</para>
    ///
    /// <para><b>Confidence is agreement, corrected for chance.</b> The first version scored how far
    /// the winning lag stood above the others and gave 0.658 on random noise - a confidently wrong
    /// answer, which is the exact failure this number exists to prevent. It now asks how many of
    /// the beats the grid predicts have a hit on them, and then subtracts the score a grid would
    /// get by luck. On dense material that correction is the whole difference: hitting 60% of
    /// beats means nothing if 50% of all hops have energy in them.</para></summary>
    private static Tempo FindTempo(double[] flux, double hopSeconds)
    {
        if (flux.Length < 16) return new Tempo(0, 0, 0);

        // Centred, so a loud passage does not read as periodicity.
        var mean = flux.Average();
        var centred = flux.Select(f => f - mean).ToArray();

        var minLag = Math.Max(1, (int)Math.Round(60 / MaxBpm / hopSeconds));
        var maxLag = Math.Min(centred.Length / 2, (int)Math.Round(60 / MinBpm / hopSeconds));
        if (maxLag <= minLag) return new Tempo(0, 0, 0);

        var scores = new double[maxLag + 2];
        for (var lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (var i = 0; i + lag < centred.Length; i++) sum += centred[i] * centred[i + lag];

            // Divided by the FULL length, not by the overlap. Normalising by (length - lag) makes
            // a long lag look better simply for having fewer terms, which was the other half of
            // where the octave error came from.
            scores[lag] = sum / centred.Length;
        }

        var peakLag = minLag;
        for (var lag = minLag; lag <= maxLag; lag++) if (scores[lag] > scores[peakLag]) peakLag = lag;
        if (scores[peakLag] <= 0) return new Tempo(0, 0, 0);

        var refined = Interpolate(scores, peakLag, minLag, maxLag);

        // The refined period, and the faster ones it might be a multiple of.
        var candidates = new List<double> { refined };
        foreach (var divisor in new[] { 2.0, 3.0, 4.0 })
        {
            var faster = refined / divisor;
            if (faster >= minLag && 60 / (faster * hopSeconds) <= MaxBpm) candidates.Add(faster);
        }

        var chance = Chance(flux);
        var judged = candidates
            .Select(period =>
            {
                var phase = FirstBeat(flux, period, hopSeconds);
                return (Period: period, Phase: phase,
                        Score: Agreement(flux, period, phase, hopSeconds, chance));
            })
            .ToList();

        var strongest = judged.Max(c => c.Score);
        if (strongest <= 0) return new Tempo(0, 0, 0);

        // The fastest candidate that explains the track about as well as the best one does.
        var chosen = judged.Where(c => c.Score >= strongest * 0.9).MinBy(c => c.Period);

        // Then fit that grid to the onsets it is meant to describe. Autocorrelation locates a
        // period to about half a percent, which sounds close and is not: 128.81 BPM against a true
        // 128 walks 76ms - more than two frames - across twelve seconds, and the far end of the
        // grid sits on nothing. Measured, before this existed.
        var (period, phase) = Fit(chosen.Period, chosen.Phase / hopSeconds, Peaks(flux, hopSeconds));

        return new Tempo(
            Math.Round(60 / (period * hopSeconds), 2),
            Math.Round(Math.Clamp(Agreement(flux, period, phase * hopSeconds, hopSeconds, chance), 0, 1), 3),
            Math.Round(phase * hopSeconds, 6));
    }

    /// <summary>Least squares through the onsets that fall near the grid: each onset is assigned
    /// the beat number nearest to it, and the line through (beat number, time) gives a period and a
    /// phase far more precise than the search that proposed them.
    ///
    /// <para>Only onsets within a quarter of a period of a beat take part, so off-beat playing
    /// pulls the grid nowhere. Run twice, because the assignment improves once the period does.</para></summary>
    private static (double Period, double Phase) Fit(double period, double phase, List<int> peaks)
    {
        if (peaks.Count < 4) return (period, phase);

        for (var pass = 0; pass < 2; pass++)
        {
            double n = 0, sumIndex = 0, sumTime = 0, sumIndexSquared = 0, sumProduct = 0;

            foreach (var h in peaks)
            {
                var exact = (h - phase) / period;
                var index = Math.Round(exact);
                if (Math.Abs(exact - index) > 0.25) continue;

                n++;
                sumIndex += index;
                sumTime += h;
                sumIndexSquared += index * index;
                sumProduct += index * h;
            }

            if (n < 4) return (period, phase);

            var denominator = n * sumIndexSquared - sumIndex * sumIndex;
            if (Math.Abs(denominator) < 1e-9) return (period, phase);

            var slope = (n * sumProduct - sumIndex * sumTime) / denominator;
            var intercept = (sumTime - slope * sumIndex) / n;

            // A fit that moves the period by more than a tenth has locked onto something else.
            if (slope <= 0 || Math.Abs(slope - period) > period * 0.1) return (period, phase);

            period = slope;
            phase = intercept;
        }

        // Bring the phase back to the first beat at or after zero.
        while (phase - period >= 0) phase -= period;
        while (phase < 0) phase += period;

        return (period, phase);
    }

    /// <summary>Parabolic interpolation through the peak and its two neighbours, which recovers the
    /// fraction of a hop the true period sits at.</summary>
    private static double Interpolate(double[] scores, int peak, int minLag, int maxLag)
    {
        if (peak <= minLag || peak >= maxLag) return peak;

        double a = scores[peak - 1], b = scores[peak], c = scores[peak + 1];
        var denominator = a - 2 * b + c;

        if (Math.Abs(denominator) < 1e-12) return peak;

        var offset = 0.5 * (a - c) / denominator;
        return Math.Abs(offset) > 1 ? peak : peak + offset;
    }

    /// <summary>The fraction of hops carrying real energy - what a grid would agree with by luck.</summary>
    private static double Chance(double[] flux)
    {
        var peak = flux.Length > 0 ? flux.Max() : 0;
        if (peak <= 0) return 1;

        var threshold = peak * 0.15;
        var lit = flux.Count(f => f >= threshold);

        // Each beat is tested against a three-hop window, so luck gets three chances per beat.
        return Math.Clamp((double)lit * 3 / flux.Length, 0, 1);
    }

    /// <summary>How many of the beats this grid predicts have a hit on them, above chance.</summary>
    private static double Agreement(
        double[] flux, double period, double firstBeat, double hopSeconds, double chance)
    {
        if (flux.Length == 0 || period <= 0) return 0;

        var peak = flux.Max();
        if (peak <= 0) return 0;

        // A beat counts as played when there is energy within one hop of it: the grid is regular
        // and a drummer is not, so an exact-hop test fails on anything human.
        var threshold = peak * 0.15;
        int beats = 0, hits = 0;

        for (var t = firstBeat; t < flux.Length * hopSeconds; t += period * hopSeconds)
        {
            var h = (int)Math.Round(t / hopSeconds);
            if (h >= flux.Length) break;

            beats++;
            for (var near = Math.Max(0, h - 1); near <= Math.Min(flux.Length - 1, h + 1); near++)
                if (flux[near] >= threshold) { hits++; break; }
        }

        if (beats < 4) return 0;

        var agreement = (double)hits / beats;
        return chance >= 1 ? 0 : Math.Clamp((agreement - chance) / (1 - chance), 0, 1);
    }

    /// <summary>Where the grid starts: the offset within one period whose beats collect the most
    /// onset energy.</summary>
    private static double FirstBeat(double[] flux, double period, double hopSeconds)
    {
        double bestScore = -1;
        var bestOffset = 0;

        for (var offset = 0; offset < Math.Ceiling(period); offset++)
        {
            double score = 0;
            for (double t = offset; t < flux.Length; t += period)
            {
                var h = (int)Math.Round(t, MidpointRounding.AwayFromZero);
                if (h < flux.Length) score += flux[h];
            }

            if (score > bestScore) { bestScore = score; bestOffset = offset; }
        }

        return bestOffset * hopSeconds;
    }

    /// <summary>The grid, laid across the whole track, each beat carrying the energy actually
    /// found there - so a beat the drummer left out reads weak rather than absent.</summary>
    private static IEnumerable<Cue> Beats(
        Tempo tempo, double[] flux, double hopSeconds, double seconds, double fps)
    {
        var period = 60 / tempo.Bpm;
        var peak = flux.Length > 0 ? flux.Max() : 0;
        var index = 0;

        for (var t = tempo.FirstBeat; t < seconds; t += period, index++)
        {
            var h = (int)Math.Round(t / hopSeconds);
            var strength = peak > 0 && h >= 0 && h < flux.Length ? flux[h] / peak : 0;

            yield return Snap(t, index % 4 == 0 ? CueKind.Downbeat : CueKind.Beat, strength, fps);
        }
    }

    // ---- silence ----------------------------------------------------------------------------

    /// <summary>Where sound starts and stops.
    ///
    /// <para>The threshold is a fraction of the track's own loud level rather than an absolute dB,
    /// because a quietly mastered voiceover and a loud one should give the same boundaries.</para>
    ///
    /// <para><b>Short runs are absorbed before any boundary is emitted</b>, and that ordering is
    /// the whole of it. Testing each run's length as it ends is not enough: an 80ms breath between
    /// two words is correctly not reported as the end of the speech, and then the words after it
    /// are a new run and report a new START. One pause, one spurious cue. So the gaps are closed
    /// and the specks removed first, and only what survives becomes a boundary.</para></summary>
    private static IEnumerable<Cue> Silences(double[] rms, double hopSeconds, double fps)
    {
        if (rms.Length == 0) yield break;

        var sorted = rms.Order().ToArray();
        var loud = sorted[(int)(sorted.Length * 0.9)];      // the 90th percentile, not the max
        if (loud <= 0) yield break;

        var threshold = loud * 0.08;
        var minimumHops = Math.Max(1, (int)Math.Ceiling(0.25 / hopSeconds));

        var sounding = new bool[rms.Length];
        for (var h = 0; h < rms.Length; h++) sounding[h] = rms[h] > threshold;

        Absorb(sounding, minimumHops, of: false);   // close the gaps that are too short to be gaps
        Absorb(sounding, minimumHops, of: true);    // then drop the blips too short to be speech

        for (var h = 1; h < sounding.Length; h++)
        {
            if (sounding[h] == sounding[h - 1]) continue;
            yield return Snap(h * hopSeconds, sounding[h] ? CueKind.SoundStart : CueKind.SoundEnd, 1, fps);
        }
    }

    /// <summary>Flip any run of <paramref name="of"/> shorter than <paramref name="minimum"/> to
    /// its opposite. A run touching either end is left alone - a track that fades in over 100ms has
    /// not told us anything about how long its first phrase is.</summary>
    private static void Absorb(bool[] values, int minimum, bool of)
    {
        var run = 0;
        for (var i = 0; i <= values.Length; i++)
        {
            if (i < values.Length && values[i] == of) { run++; continue; }

            var started = i - run;
            if (run > 0 && run < minimum && started > 0 && i < values.Length)
                for (var j = started; j < i; j++) values[j] = !of;

            run = 0;
        }
    }

    // ---- frames -----------------------------------------------------------------------------

    /// <summary>The loudness envelope at ONE VALUE PER FRAME, normalised 0-1.
    ///
    /// <para>Per frame rather than per hop because the consumer is a renderer: an envelope at the
    /// analysis rate would have to be resampled by whoever used it, and they would each do it
    /// differently.</para></summary>
    private static double[] Decimate(double[] rms, double hopSeconds, double seconds, double fps)
    {
        var frames = (int)Math.Round(seconds * fps);
        if (frames <= 0 || rms.Length == 0) return [];

        var peak = rms.Max();
        var envelope = new double[frames];

        for (var f = 0; f < frames; f++)
        {
            var h = (int)Math.Round(f / fps / hopSeconds);
            envelope[f] = peak <= 0 ? 0 : Math.Round(rms[Math.Clamp(h, 0, rms.Length - 1)] / peak, 4);
        }

        return envelope;
    }

    /// <summary>Move a cue to the nearest frame and record how far it went.
    ///
    /// <para>The same rule <see cref="ClipPlanner"/> applies to clip lengths, for the same reason:
    /// a frame is the smallest thing a render has, so a cue between two of them is not actionable,
    /// and the amount it moved is the author's business rather than something to absorb.</para></summary>
    private static Cue Snap(double at, CueKind kind, double strength, double fps)
    {
        var frame = (int)Math.Round(at * fps, MidpointRounding.AwayFromZero);
        var snapped = frame / fps;

        return new Cue(
            Math.Round(snapped, 6),
            frame,
            kind,
            Math.Round(Math.Clamp(strength, 0, 1), 4),
            Math.Round((snapped - at) * 1000, 3));
    }
}
