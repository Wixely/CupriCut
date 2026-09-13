using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CupriCut.Services;
using SkiaSharp;

namespace CupriCut.Tools;

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        // camelCase, matching what a .cut.json file on disk looks like. Without it a nested POCO
        // such as RenderSettings comes back as "Width" from load_project while the file it was
        // read from says "width", and an agent has to learn both shapes for one thing.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };
}

/// <summary>Argument shapes every render tool shares: which frames, what colour, what the answer
/// says about what it cost.</summary>
internal static class ToolSupport
{
    /// <summary>The times a range asks for. The sweep still starts at zero whatever
    /// <paramref name="from"/> says - that is what makes the kept frames match the video - so this
    /// is only about which frames are handed back.</summary>
    public static double[] Range(double from, double to, double fps)
    {
        if (fps <= 0) throw new ArgumentException("fps must be greater than zero.", nameof(fps));
        if (to < from) throw new ArgumentException($"to ({to}) is before from ({from}).", nameof(to));
        var count = (int)Math.Floor((to - from) * fps + 1e-9);
        var times = new double[count + 1];
        for (var i = 0; i <= count; i++) times[i] = Math.Round(from + i / fps, 6, MidpointRounding.AwayFromZero);
        return times;
    }

    /// <summary>The times a contact sheet asks for: an explicit list, or a duration sampled either
    /// every <paramref name="every"/> seconds or into <paramref name="count"/> even steps.</summary>
    public static double[] SampleTimes(double[]? times, double? every, double? duration, int? count)
    {
        if (times is { Length: > 0 })
            return [.. times.Select(t => Math.Round(t, 6, MidpointRounding.AwayFromZero)).Distinct().Order()];

        if (duration is not { } span || span < 0)
            throw new ArgumentException("Give either times, or a duration with every or count.", nameof(duration));

        if (every is { } step)
        {
            if (step <= 0) throw new ArgumentException("every must be greater than zero.", nameof(every));
            var n = (int)Math.Floor(span / step + 1e-9);
            return [.. Enumerable.Range(0, n + 1).Select(i => Math.Round(i * step, 6, MidpointRounding.AwayFromZero))];
        }

        var cells = count is { } c && c > 0 ? c : 9;
        if (cells == 1) return [0];
        return [.. Enumerable.Range(0, cells).Select(i => Math.Round(i * span / (cells - 1), 6, MidpointRounding.AwayFromZero))];
    }

    /// <summary>A CSS-ish colour for the frame's clear. Null or blank means the tool's default.</summary>
    public static SKColor? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (SKColor.TryParse(value.Trim(), out var colour)) return colour;
        throw new ArgumentException($"'{value}' is not a colour Skia recognises. Use #RGB, #RRGGBB, #AARRGGBB or a named colour.", nameof(value));
    }

    /// <summary>Downscale to a target width, preserving aspect. Returns the original when it is
    /// already no wider.</summary>
    public static SKBitmap Thumbnail(SKBitmap source, int targetWidth)
    {
        if (targetWidth <= 0 || source.Width <= targetWidth) return source;
        var height = Math.Max(1, (int)Math.Round(source.Height * (targetWidth / (double)source.Width)));
        var info = new SKImageInfo(targetWidth, height, source.ColorType, source.AlphaType);
        var scaled = source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (scaled is null) return source;
        source.Dispose();
        return scaled;
    }

    /// <summary>What the sweep cost, in the terms the decision to sweep was made in. Every tool
    /// says this, because "0.5s to preview t=3s" is the number that explains the design.</summary>
    public static object Cost(SweepReport report) => new
    {
        framesSwept = report.StepsRendered,
        framesKept = report.FramesKept,
        sweptFrom = 0.0,
        sweptTo = report.LastTime,
        sweepFps = report.SweepFps,
        elapsedMs = Math.Round(report.ElapsedMs, 1),
        msPerFrame = Math.Round(report.ElapsedMs / Math.Max(1, report.StepsRendered), 2),
        size = $"{report.Width * report.Scale}x{report.Height * report.Scale}",
        settled = report.Settled,
        fontProblems = report.FontProblems.Count == 0 ? null : report.FontProblems,
    };

    public static string Seconds(double t) => t.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A file name that sorts in frame order and says what it is.</summary>
    public static string FrameName(string prefix, int index, double time) =>
        $"{prefix}_{index:D5}_t{time.ToString("0.000", CultureInfo.InvariantCulture).Replace('.', 'p')}.png";

    /// <summary>Strip a caller-supplied name down to something safe to use as a file name stem.
    /// A project's <c>.cut.json</c> is a two-part extension, so one pass of
    /// <c>GetFileNameWithoutExtension</c> would leave "hero.cut" behind.</summary>
    public static string SafeStem(string? given, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(given) ? fallback : Path.GetFileName(given.Trim());
        var stem = CutProject.IsProjectPath(name)
            ? name[..^CutProject.Extension.Length]
            : Path.GetFileNameWithoutExtension(name);
        var cleaned = new string([.. stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }
}
