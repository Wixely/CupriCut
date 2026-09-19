using System.Diagnostics;
using System.Text;

namespace CupriCut.Services;

/// <summary>What one look at a piece of footage turns up.</summary>
/// <param name="Source">What was analysed.</param>
/// <param name="Seconds">How long it is.</param>
/// <param name="Fps">The rate the cues are snapped to, and the rate the envelopes are sampled at.</param>
/// <param name="Cues">Scene changes, in time order.</param>
/// <param name="Motion">How much the picture changed at each frame, 0-1. For finding the calm part
/// of a shot to put text on.</param>
/// <param name="Luminance">How bright each frame is, 0-1. For deciding whether text over it should
/// be light or dark, and where a lower third will actually be legible.</param>
public sealed record VideoAnalysis(
    string Source,
    double Seconds,
    double Fps,
    IReadOnlyList<Cue> Cues,
    IReadOnlyList<double> Motion,
    IReadOnlyList<double> Luminance)
{
    public int Count(CueKind kind) => Cues.Count(c => c.Kind == kind);

    public IEnumerable<Cue> Of(CueKind kind) => Cues.Where(c => c.Kind == kind);

    /// <summary>
    /// The quietest stretch of this length: where the picture moves least, so text put there is
    /// not fighting the footage.
    ///
    /// <para><b>It will not straddle a cut.</b> A window either side of a scene change averages
    /// two shots and can score beautifully while being the worst possible place to put anything -
    /// the text would appear over one shot and end over another.</para>
    /// </summary>
    /// <param name="seconds">How long the caption needs to be on screen.</param>
    /// <returns>When to start it, and how much movement is there. <c>At</c> is -1 when the footage
    /// is shorter than the window asked for.</returns>
    public (double At, double Motion) CalmestWindow(double seconds)
    {
        var width = (int)Math.Round(seconds * Fps);
        if (width <= 0 || Motion.Count < width) return (-1, 0);

        // Frames a cut lands on, so a window can be rejected for containing one.
        var cuts = new HashSet<int>(Of(CueKind.SceneChange).Select(c => c.Frame));

        double best = double.MaxValue;
        var at = -1;

        for (var start = 0; start + width <= Motion.Count; start++)
        {
            var straddles = false;
            double total = 0;

            for (var f = start; f < start + width; f++)
            {
                // The first frame of a shot is allowed to BE the start of the window - that is
                // exactly where a caption belongs. It is a cut in the middle that disqualifies it.
                if (f > start && cuts.Contains(f)) { straddles = true; break; }
                total += Motion[f];
            }

            if (straddles) continue;

            var average = total / width;
            if (average >= best) continue;

            best = average;
            at = start;
        }

        return at < 0 ? (-1, 0) : (Math.Round(at / Fps, 6), Math.Round(best, 4));
    }
}

/// <summary>
/// Where the cuts are in a piece of footage, and where it is calm enough to put words over.
///
/// <para><b>The opposite problem to <see cref="AudioCues"/>.</b> That one times motion to music;
/// this one keeps a caption from landing on a cut, and finds the part of a shot still enough to
/// read text over. Same <see cref="Cue"/> record, different source - a caller that already knows
/// how to act on a time and a frame number needs to learn nothing new.</para>
///
/// <para><b>Measured in-process from tiny frames.</b> ffmpeg decodes the footage to 32x18 greyscale
/// and everything else is arithmetic here: mean luminance per frame, mean absolute difference
/// between consecutive frames for motion, and the same adaptive peak-picking the audio onsets use
/// for the cuts. Not ffmpeg's own <c>scdet</c>, for the reason every other analysis here is
/// in-process: a filter whose threshold behaviour changes between builds makes a render
/// irreproducible, and 32x18 is enough to see a cut while being small enough that a ten-minute clip
/// is a few megabytes rather than several gigabytes.</para>
///
/// <para><b>Why that is enough.</b> A cut changes nearly every pixel at once; a pan, however fast,
/// changes them gradually. At 576 pixels the difference is still enormous, and throwing away the
/// detail removes exactly the noise - grain, compression - that makes full-resolution differencing
/// jumpy.</para>
/// </summary>
public static class VideoCues
{
    /// <summary>The frame the analysis works on. Small on purpose - see the class note.</summary>
    public const int Width = 32;

    public const int Height = 18;

    /// <summary>Read a piece of footage.</summary>
    /// <param name="ffmpegPath">From <c>Cut:FfmpegPath</c>.</param>
    /// <param name="path">An absolute path, already resolved against the composition roots.</param>
    /// <param name="fps">The rate to sample at, and the rate cues are snapped to.</param>
    /// <param name="maxSeconds">A ceiling, so a mistaken argument cannot read a feature film.</param>
    public static VideoAnalysis Analyse(string ffmpegPath, string path, double fps, double maxSeconds = 1800)
    {
        var frames = Decode(ffmpegPath, path, fps, maxSeconds);
        return Analyse(frames, fps, Path.GetFileName(path));
    }

    /// <summary>The half with no ffmpeg in it: greyscale frames in, cues out. Separate so it can be
    /// tested against footage built to order rather than against a recording.</summary>
    public static VideoAnalysis Analyse(IReadOnlyList<byte[]> frames, double fps, string source = "")
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

        var motion = new double[frames.Count];
        var luminance = new double[frames.Count];

        for (var f = 0; f < frames.Count; f++)
        {
            long sum = 0;
            for (var i = 0; i < frames[f].Length; i++) sum += frames[f][i];
            luminance[f] = Math.Round(sum / (255.0 * frames[f].Length), 4);

            if (f == 0) continue;

            long difference = 0;
            for (var i = 0; i < frames[f].Length; i++) difference += Math.Abs(frames[f][i] - frames[f - 1][i]);
            motion[f] = Math.Round(difference / (255.0 * frames[f].Length), 4);
        }

        return new VideoAnalysis(
            source,
            Math.Round(frames.Count / fps, 6),
            fps,
            [.. Cuts(motion, fps)],
            motion,
            luminance);
    }

    /// <summary>A frame that differs from the one before it far more than its neighbourhood does.
    ///
    /// <para>The threshold is local, for the same reason the audio one is: a fixed number finds
    /// every cut in a static interview and none at all in a handheld chase. A cut has to stand
    /// above the movement AROUND it, not above some absolute idea of movement.</para></summary>
    private static IEnumerable<Cue> Cuts(double[] motion, double fps)
    {
        if (motion.Length < 3) yield break;

        var peak = motion.Max();
        if (peak <= 0) yield break;

        const int Neighbourhood = 12;

        // Frame 0 has no predecessor, so its motion is 0 and it is never a peak. That is correct:
        // the start of a clip is not a cut, it is where the clip starts.
        for (var f = 1; f < motion.Length; f++)
        {
            if (f > 1 && motion[f] < motion[f - 1]) continue;
            if (f < motion.Length - 1 && motion[f] < motion[f + 1]) continue;

            var from = Math.Max(1, f - Neighbourhood);
            var to = Math.Min(motion.Length, f + Neighbourhood + 1);
            var local = Median(motion, from, to);

            // A cut is a step change: several times the surrounding movement AND a large absolute
            // difference. Either test alone misfires - the first on a still shot where the median
            // is near zero, the second on footage that is violent throughout.
            if (motion[f] < local * 3 + 0.02) continue;
            if (motion[f] < 0.08) continue;

            yield return Snap(f, motion[f] / peak, fps);
        }
    }

    private static double Median(double[] values, int from, int to)
    {
        var slice = values[from..to];
        Array.Sort(slice);
        return slice.Length == 0 ? 0 : slice[slice.Length / 2];
    }

    /// <summary>A frame index is already on a frame, so there is nothing to snap - but a cue says
    /// how far it moved, and being honest that the answer is zero is better than leaving it out.</summary>
    private static Cue Snap(int frame, double strength, double fps) =>
        new(Math.Round(frame / fps, 6), frame, CueKind.SceneChange,
            Math.Round(Math.Clamp(strength, 0, 1), 4), 0);

    /// <summary>ffmpeg decoding the footage to tiny greyscale frames.</summary>
    private static List<byte[]> Decode(string ffmpegPath, string path, double fps, double maxSeconds)
    {
        if (!File.Exists(path)) throw new CutPolicyException($"No video file at '{path}'.");

        var size = Width * Height;
        var args = string.Join(' ',
            "-v", "error",
            "-i", path.Contains(' ') ? $"\"{path}\"" : path,
            "-an",
            "-t", maxSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            "-vf", $"fps={fps.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)},scale={Width}:{Height},format=gray",
            "-f", "rawvideo",
            "-");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffmpegPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new CutPolicyException(
                $"Could not start ffmpeg at '{ffmpegPath}': {ex.Message}. "
                + "Set Cut:FfmpegPath, or call probe to see what this machine has.");
        }

        process.BeginErrorReadLine();

        // Drained before waiting: ffmpeg blocks once the pipe fills, so waiting first deadlocks on
        // anything longer than a second or two.
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new CutPolicyException(
                $"ffmpeg could not decode '{Path.GetFileName(path)}' (exit {process.ExitCode}). {Tail(stderr.ToString())}");
        }

        var bytes = buffer.ToArray();
        if (bytes.Length < size)
            throw new CutPolicyException($"'{Path.GetFileName(path)}' decoded to no frames at all. {Tail(stderr.ToString())}");

        var frames = new List<byte[]>(bytes.Length / size);
        for (var offset = 0; offset + size <= bytes.Length; offset += size)
            frames.Add(bytes[offset..(offset + size)]);

        return frames;
    }

    private static string Tail(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : string.Join(" ", lines.TakeLast(3));
    }
}
