using System.Globalization;

namespace CupriCut.Services;

/// <summary>
/// What a requested clip length actually became, once it was made a whole number of frames.
///
/// <para>A clip is frames, so its length is quantised to <c>1/fps</c>. Most of the time the request
/// lands exactly — 10s at 120fps is 1200 frames on the nose — but a fractional rate cannot:
/// 10s at 29.97fps is 299.7 frames, and there is no such thing. The nearest whole frame count wins,
/// and the difference is <b>always reported</b> rather than absorbed, because a renderer quietly
/// handing back 10.010s when it was asked for 10.000s is the kind of thing that is discovered much
/// later, by someone cutting to music.</para>
/// </summary>
public sealed record ClipPlan(
    double RequestedSeconds,
    double ActualSeconds,
    int Frames,
    double Fps,
    bool Exact,
    string? Note)
{
    /// <summary>Signed difference in milliseconds: positive means the clip is longer than asked.</summary>
    public double DeltaMs => Math.Round((ActualSeconds - RequestedSeconds) * 1000, 4);
}

public static class ClipPlanner
{
    /// <summary>
    /// The frame times for a clip of <paramref name="requestedSeconds"/>, plus what it actually
    /// came to.
    ///
    /// <para>Frames cover <c>[from, from + actual)</c> — exclusive of the end, so
    /// <c>frames / fps</c> is the length exactly. See <c>ToolSupport.Range</c> for the inclusive
    /// sampling counterpart, which is one frame longer.</para>
    /// </summary>
    public static (double[] Times, ClipPlan Plan) Plan(double from, double requestedSeconds, double fps)
    {
        if (fps <= 0) throw new ArgumentException("fps must be greater than zero.", nameof(fps));
        if (requestedSeconds <= 0) throw new ArgumentException("duration must be greater than zero.", nameof(requestedSeconds));

        var exactFrames = requestedSeconds * fps;

        // Nearest whole frame. Rounded rather than truncated because 10 * 120 is 1199.9999999999998
        // in binary, and truncating there would silently drop a frame from an exact request.
        var frames = (int)Math.Round(exactFrames, MidpointRounding.AwayFromZero);

        string? note = null;
        if (frames < 1)
        {
            // Shorter than a single frame. One frame is the least a clip can be, and saying so is
            // better than returning an empty sweep or a zero-length file.
            frames = 1;
            note = $"{Seconds(requestedSeconds)}s at {Rate(fps)} fps is {exactFrames:0.###} frames, which is less than one. " +
                   $"Rendered the single frame a clip must have, so it is {Seconds(1 / fps)}s.";
        }

        var actual = frames / fps;
        var exact = note is null && Math.Abs(actual - requestedSeconds) < 1e-9;

        note ??= exact ? null : Describe(requestedSeconds, actual, exactFrames, frames, fps);

        var times = new double[frames];
        for (var i = 0; i < frames; i++)
            times[i] = Math.Round(from + i / fps, 6, MidpointRounding.AwayFromZero);

        return (times, new ClipPlan(requestedSeconds, actual, frames, fps, exact, note));
    }

    private static string Describe(double requested, double actual, double exactFrames, int frames, double fps)
    {
        var deltaMs = (actual - requested) * 1000;
        var sign = deltaMs >= 0 ? "+" : "-";

        var note = $"{Seconds(requested)}s at {Rate(fps)} fps is {exactFrames:0.###} frames, not a whole number. " +
                   $"Snapped to the nearest whole frame ({frames}), so the clip is {Seconds(actual)}s " +
                   $"({sign}{Math.Abs(deltaMs):0.###}ms).";

        // A whole-number rate that divides the request exactly is the fix most people want, so name
        // one when it exists rather than leaving them to work it out.
        var suggestion = NearestExactRate(requested, fps);
        if (suggestion is { } rate)
            note += $" For exactly {Seconds(requested)}s, use {Rate(rate)} fps ({requested * rate:0} frames).";
        else
            note += $" For an exact length at {Rate(fps)} fps, ask for {Seconds(actual)}s.";

        return note;
    }

    /// <summary>A frame rate near <paramref name="fps"/> that divides the request into whole
    /// frames — typically the integer rate a fractional one is approximating (29.97 → 30).</summary>
    private static double? NearestExactRate(double requestedSeconds, double fps)
    {
        foreach (var candidate in new[] { Math.Round(fps), Math.Ceiling(fps), Math.Floor(fps) })
        {
            if (candidate <= 0 || Math.Abs(candidate - fps) > 1.0) continue;
            var frames = requestedSeconds * candidate;
            if (Math.Abs(frames - Math.Round(frames)) < 1e-9) return candidate;
        }
        return null;
    }

    private static string Seconds(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Rate(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
