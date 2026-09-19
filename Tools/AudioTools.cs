using System.ComponentModel;
using System.Text.Json;
using CupriCut.Services;
using ModelContextProtocol.Server;

namespace CupriCut.Tools;

/// <summary>Where the hits are in a track, so motion can be written onto them.</summary>
[McpServerToolType]
public static class AudioTools
{
    [McpServerTool(Name = "analyse_audio"),
     Description("""
        Listen to an audio file and report the moments worth timing animation to: onsets (hits),
        a tempo grid when there is one, where sound starts and stops, and a loudness envelope.

        Every cue carries a TIME and the FRAME it lands on at the rate you asked for, because a
        frame is the smallest thing a render has. Write the keyframes at those times: a title that
        lands on frame 72 is animation-delay 2.4s at 30fps.

        READ THE TEMPO CONFIDENCE. Beat detection is reliable on percussive material and unreliable
        on anything else, so the confidence is not decoration - below 0.35 no beats are emitted at
        all and only onsets and silence boundaries are worth using. Onsets and silence boundaries
        are the dependable half, and silence boundaries are usually what a lower third wants.

        The analysis does not change the composition. It answers a question; you decide what the
        answer means.
        """)]
    public static string AnalyseAudio(
        CupriCutService cut,
        [Description("Audio file, relative to a composition root. Any format ffmpeg reads.")]
        string audio,
        [Description("Frame rate the cues are snapped to. Defaults to the configured rate.")]
        double fps = 0,
        [Description("Only report cues of this kind: onset, beat, downbeat, soundstart, soundend.")]
        string? kind = null,
        [Description("Include the per-frame loudness envelope. Long, so off unless asked for.")]
        bool envelope = false,
        [Description("Stop after this many seconds of audio.")]
        double maxSeconds = 1800)
    {
        cut.EnsureVideoAllowed();

        var rate = fps > 0 ? fps : cut.Options.DefaultFps;
        var path = cut.ResolveRead(audio);
        var analysis = AudioDecoder.Analyse(cut.Options.FfmpegPath, path, rate, maxSeconds);

        var wanted = Parse(kind);
        var cues = wanted is null ? analysis.Cues : [.. analysis.Of(wanted.Value)];

        return JsonSerializer.Serialize(new
        {
            source = analysis.Source,
            seconds = analysis.Seconds,
            fps = analysis.Fps,
            frames = (int)Math.Round(analysis.Seconds * analysis.Fps),

            tempo = new
            {
                bpm = analysis.Tempo.Bpm,
                confidence = analysis.Tempo.Confidence,
                usable = analysis.Tempo.Usable,
                firstBeat = Math.Round(analysis.Tempo.FirstBeat, 6),
                note = analysis.Tempo.Usable
                    ? "Beats are emitted. Check individual strengths - a weak one is a beat nothing was played on."
                    : "No usable beat. Use onsets and sound boundaries; the bpm above is what the search found and is not to be relied on.",
            },

            counts = Enum.GetValues<CueKind>().ToDictionary(k => k.ToString(), analysis.Count),

            cues = cues.Select(c => new
            {
                kind = c.Kind.ToString(),
                at = c.At,
                frame = c.Frame,
                strength = c.Strength,
                snapMs = c.SnapMs,
            }),

            // One value per frame, 0-1, for anything that should breathe with the track rather
            // than snap to it. Off by default because a three-minute song is 5400 numbers.
            loudness = envelope ? analysis.Loudness : null,
        }, JsonOpts.Default);
    }

    private static CueKind? Parse(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;

        var cleaned = kind.Trim().Replace("-", "").Replace("_", "").Replace(" ", "");
        foreach (var value in Enum.GetValues<CueKind>())
            if (string.Equals(value.ToString(), cleaned, StringComparison.OrdinalIgnoreCase)) return value;

        throw new ArgumentException(
            $"Unknown cue kind '{kind}'. Known: {string.Join(", ", Enum.GetValues<CueKind>().Select(v => v.ToString().ToLowerInvariant()))}.");
    }
}
