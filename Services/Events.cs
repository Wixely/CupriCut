using System.Globalization;
using System.Text.RegularExpressions;

namespace CupriCut.Services;

/// <summary>One moment a composition says is worth knowing about.</summary>
/// <param name="At">When, in seconds from the start of the film.</param>
/// <param name="Name">What the author called it. Not interpreted - it means whatever the thing
/// reading the sidecar decides it means.</param>
/// <param name="Tag">The element that declared it.</param>
/// <param name="Classes">What the author called that element, which is how they will recognise it.</param>
/// <param name="Track">The <c>data-track</c> it sits on, when it is on one.</param>
/// <param name="Relative">True when it was written as an offset from the element's own start
/// rather than as an absolute time. Reporting only - <see cref="At"/> is already resolved.</param>
public sealed record CutEvent(
    double At, string Name, string Tag, string? Classes, string? Track, bool Relative)
{
    /// <summary>The frame this lands on at a given rate - which is the number anything acting on
    /// an event actually needs. Rounded to nearest, so an event at 2.4s in a 30 fps film is frame
    /// 72 and not frame 71.</summary>
    public int Frame(double fps) => fps > 0 ? (int)Math.Round(At * fps, MidpointRounding.AwayFromZero) : 0;

    public string Describe()
    {
        var what = Classes is { Length: > 0 } ? $"<{Tag} class=\"{Classes}\">" : $"<{Tag}>";
        var where = Track is { Length: > 0 } ? $"{what} on {Track}" : what;
        return $"{Name} at {TimelineWindow.Seconds(At)}s - {where}";
    }
}

/// <summary>What a composition's declared events came to.</summary>
public sealed record EventPlan(IReadOnlyList<CutEvent> Events, IReadOnlyList<string> Problems)
{
    public bool Any => Events.Count > 0;

    /// <summary>The distinct names, in the order they were first seen.</summary>
    public IReadOnlyList<string> Names =>
        [.. Events.Select(e => e.Name).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// <c>data-cut-event</c>: the moments a composition wants something to happen at.
///
/// <para><b>Declared and reported, never executed.</b> An event does not run anything and does not
/// touch the render - <c>inspect</c> lists them and a render writes them out beside the file, and
/// then it is over to whatever is driving the render. The composition says WHEN; the caller
/// decides what.</para>
///
/// <para><b>Why not callbacks.</b> The obvious design is a hook the engine fires as the clock
/// passes a mark, and it cannot work here. Nothing plays: a render seeks. Frames come out of
/// <c>Animate(t)</c> at whatever order and granularity the sweep chooses, workers render different
/// stretches of the same film at the same time, a re-render of one bad second starts at t=4.0, and
/// a composition that is pure in <c>t</c> never gets swept at all - its frames are rendered
/// directly. A callback would fire out of order, fire many times, or never fire. The same is true
/// upstream, which is why HyperFrames' own render path has no such hook either.</para>
///
/// <para>A number and a name in a file has none of those problems. It is correct under every one
/// of those cases because it is not tied to the traversal at all, it survives the render (a
/// callback does not - the render is a file, and the file is what gets used later), and the thing
/// acting on it is a program the author already controls.</para>
///
/// <para><b>Times may be absolute or relative.</b> <c>data-cut-event="2.4:bar-full"</c> is 2.4s
/// into the film. <c>data-cut-event="+1.2:bar-full"</c> is 1.2s after the element's own
/// <c>data-start</c>, which is what you want inside a timed scene - the same reason those scenes
/// publish <c>--cut-start</c> to their children. An element with no <c>data-start</c> starts at
/// zero, so on an untimed element the two forms mean the same thing.</para>
///
/// <para>Several go in one attribute, separated by semicolons.</para>
/// </summary>
public static partial class Events
{
    /// <summary>The attribute an author writes.</summary>
    public const string Attribute = "data-cut-event";

    // Deliberately permissive about the name and strict about the time: a name is the author's
    // own word for something and nothing here should have opinions about it, but a time that does
    // not parse is a mark that will silently never be reported.
    [GeneratedRegex("""<(?<name>[a-zA-Z][^\s/>]*)(?<attrs>(?:"[^"]*"|'[^']*'|[^>"'])*)>""")]
    private static partial Regex OpeningTag();

    [GeneratedRegex("""(?<![-\w])data-cut-event\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex EventAttr();

    [GeneratedRegex("""(?<![-\w])data-start\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex StartAttr();

    [GeneratedRegex("""(?<![-\w])(?:class|data-track)\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClassOrTrack();

    /// <summary>Everything a composition declares, in time order.</summary>
    /// <param name="html">The markup, before or after the timeline rewrites it - this reads only
    /// attributes the rewrite leaves alone.</param>
    public static EventPlan Plan(string html)
    {
        if (string.IsNullOrEmpty(html)
            || !html.Contains(Attribute, StringComparison.OrdinalIgnoreCase))
        {
            return new EventPlan([], []);
        }

        var events = new List<CutEvent>();
        var problems = new List<string>();

        foreach (Match tag in OpeningTag().Matches(html))
        {
            var attrs = tag.Groups["attrs"].Value;
            var declared = EventAttr().Match(attrs);
            if (!declared.Success) continue;

            var name = tag.Groups["name"].Value;
            var (classes, track) = Identify(attrs);
            var start = ElementStart(attrs);

            foreach (var part in declared.Groups["v"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Parse(part, start, name, classes, track, problems) is { } e) events.Add(e);
            }
        }

        // Time order, and declaration order within a moment: two events at the same instant are
        // reported the way they were written, because there is no other defensible tie-break and
        // a caller reading the sidecar top to bottom should see what the author saw.
        return new EventPlan([.. events.OrderBy(e => e.At)], problems);
    }

    /// <summary>The ones that would never be reached, given how long the film is.</summary>
    public static IEnumerable<CutEvent> PastTheEnd(this EventPlan plan, double duration) =>
        duration > 0 ? plan.Events.Where(e => e.At > duration) : [];

    /// <summary>The file a render leaves beside its output, when the composition declared
    /// anything. Returns where it went, or null when there was nothing to say.</summary>
    /// <param name="html">The composition's markup.</param>
    /// <param name="composition">Its name, for the reader of the file.</param>
    /// <param name="directory">Where the render's own output went.</param>
    /// <param name="stem">The output's name, without an extension. The sidecar takes the same one,
    /// so the two are obviously a pair in a directory listing.</param>
    /// <param name="fps">The output rate, which is what turns a time into a frame number.</param>
    /// <param name="duration">How long the render is, so events past the end can be flagged
    /// rather than silently written as unreachable frame numbers.</param>
    public static string? WriteSidecar(
        string html, string composition, string directory, string stem, double fps, double duration)
    {
        var plan = Plan(html);
        if (!plan.Any) return null;

        var path = System.IO.Path.Combine(directory, stem + ".events.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
            new EventSidecar(
                composition,
                stem,
                fps,
                Math.Round(duration, 4),
                [.. plan.Events.Select(e => new EventRecord(
                    e.Name,
                    Math.Round(e.At, 4),
                    e.Frame(fps),
                    e.Classes is { Length: > 0 } ? $"{e.Tag}.{e.Classes.Replace(' ', '.')}" : e.Tag,
                    e.Track,
                    e.At > duration && duration > 0 ? true : null))]),
            CutProject.Json));

        return path;
    }

    /// <summary>The sidecar's shape. Flat on purpose: whatever reads this is as likely to be a
    /// shell script as a program.</summary>
    /// <param name="Composition">What was rendered.</param>
    /// <param name="Output">The name the render's own files took.</param>
    /// <param name="Fps">The rate the frame numbers are in.</param>
    /// <param name="Duration">How long the render is, in seconds.</param>
    /// <param name="Events">In time order.</param>
    private sealed record EventSidecar(
        string Composition, string Output, double Fps, double Duration,
        IReadOnlyList<EventRecord> Events);

    /// <param name="Name">The author's word for it.</param>
    /// <param name="At">When, in seconds.</param>
    /// <param name="Frame">The frame it lands on at this rate - the number a caller acts on.</param>
    /// <param name="Element">Which element declared it, as a selector-ish string for a human.</param>
    /// <param name="Track">Its track, when it has one.</param>
    /// <param name="PastTheEnd">Set only when the event is later than the render, which means
    /// nothing will ever reach it. Absent otherwise rather than false, so its presence is the
    /// warning.</param>
    private sealed record EventRecord(
        string Name, double At, int Frame, string Element, string? Track, bool? PastTheEnd);

    private static CutEvent? Parse(
        string part, double start, string tag, string? classes, string? track, List<string> problems)
    {
        var text = part.Trim();
        if (text.Length == 0) return null;

        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            problems.Add($"'{text}' is not a time and a name. {Attribute} takes '2.4:name', "
                + "or '+1.2:name' for 1.2s after the element's own start.");
            return null;
        }

        var when = text[..colon].Trim();
        var name = text[(colon + 1)..].Trim();
        if (name.Length == 0)
        {
            problems.Add($"'{text}' has no name.");
            return null;
        }

        var relative = when.StartsWith('+');
        if (!double.TryParse(relative ? when[1..] : when, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var offset))
        {
            problems.Add($"'{when}' in '{text}' is not a number of seconds.");
            return null;
        }

        var at = relative ? start + offset : offset;
        if (at < 0)
        {
            problems.Add($"'{text}' lands at {TimelineWindow.Seconds(at)}s, before the film starts.");
            return null;
        }

        return new CutEvent(at, name, tag, classes, track, relative);
    }

    /// <summary>The element's own start, which is what a relative time is relative to.</summary>
    private static double ElementStart(string attrs) =>
        StartAttr().Match(attrs) is { Success: true } m
        && double.TryParse(m.Groups["v"].Value.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var start)
        && start > 0
            ? start
            : 0;

    private static (string? Classes, string? Track) Identify(string attrs)
    {
        string? classes = null, track = null;

        foreach (Match m in ClassOrTrack().Matches(attrs))
        {
            var value = m.Groups["v"].Value.Trim();
            if (value.Length == 0) continue;

            if (m.Value.TrimStart().StartsWith("data-track", StringComparison.OrdinalIgnoreCase))
                track ??= value;
            else
                classes ??= Authored(value);
        }

        return (classes, track);
    }

    /// <summary>What the author wrote, without the classes the timeline generated - those mean
    /// nothing to anyone reading a report.</summary>
    private static string? Authored(string classes)
    {
        var kept = classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(c => !c.StartsWith("cut-t", StringComparison.Ordinal))
            .ToArray();

        return kept.Length > 0 ? string.Join(' ', kept) : null;
    }
}
