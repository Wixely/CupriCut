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

    [McpServerTool(Name = "attach_audio"),
     Description("""
        Analyse a track and STORE the result in a project: the cues, the tempo, and a hash of the
        file. Do this once; from then on load_project hands the cues back for free.

        Why stored rather than re-read: a render has to be reproducible on a machine with a
        different ffmpeg, and audio analysis drifts between versions - a cue that moves by a frame
        between two machines is exactly the kind of bug this tool exists to avoid. It also saves
        paying for the analysis on every iteration of a look-adjust-look loop.

        embed=true (the default) also keeps the audio file itself in the project, which is what a
        later mux needs. That makes the project large: as a .cutpkg the bytes are stored as bytes,
        as a .cut.json they are base64 and about a third bigger. An existing .cut.json has no
        reason to stay one - pass saveAs="name.cutpkg" to write the result as a package instead.

        The hash catches the track being swapped underneath a composition timed to it - lint
        reports a mismatch as CUT008 rather than letting you find out when a cue no longer lands.
        """)]
    public static string AttachAudio(
        CupriCutService cut,
        [Description("Project name, with or without its extension.")] string name,
        [Description("Audio file, relative to a composition root.")] string audio,
        [Description("Frame rate to snap cues to. Defaults to the project's own, then the configured rate.")]
        double fps = 0,
        [Description("Keep the audio file in the project as well as the cues. Needed to mux it later.")]
        bool embed = true,
        [Description("Write the result under this name instead, e.g. 'hero.cutpkg' to move a project into a package.")]
        string? saveAs = null,
        [Description("Stop after this many seconds of audio.")] double maxSeconds = 1800)
    {
        cut.EnsureVideoAllowed();

        var project = cut.LoadProject(name);
        var path = cut.ResolveRead(audio);

        var rate = fps > 0 ? fps
            : project.Render.Fps > 0 ? project.Render.Fps
            : cut.Options.DefaultFps;

        var analysis = AudioDecoder.Analyse(cut.Options.FfmpegPath, path, rate, maxSeconds);
        var sha = AudioDecoder.Hash(path);

        string? asset = null;
        if (embed)
        {
            asset = Path.GetFileName(path);
            project.Assets[asset] =
                $"data:{CutPackage.MediaTypeOf(path)};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
        }

        project.Audio = ProjectAudio.From(analysis, sha, asset);

        // Load from one name, save under another: the only way a project that predates the
        // container gets to become one, and the moment it matters is exactly this one.
        var saved = cut.SaveProject(string.IsNullOrWhiteSpace(saveAs) ? name : saveAs, project);

        return JsonSerializer.Serialize(new
        {
            saved,
            source = analysis.Source,
            seconds = analysis.Seconds,
            fps = analysis.Fps,
            sha256 = sha,
            embedded = asset,
            bytes = new FileInfo(saved).Length,

            tempo = new
            {
                bpm = analysis.Tempo.Bpm,
                confidence = analysis.Tempo.Confidence,
                usable = analysis.Tempo.Usable,
            },

            counts = Enum.GetValues<CueKind>().ToDictionary(k => k.ToString(), analysis.Count),

            next = analysis.Tempo.Usable
                ? "load_project returns these cues from now on. Write animation-delay at the times below."
                : "No usable beat - time to the onsets and sound boundaries, which are the dependable half.",
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
