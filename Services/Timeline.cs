using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CupriCut.Services;

/// <summary>One element's window on the timeline.</summary>
/// <param name="Index">Its position in document order, and the number in its generated class.</param>
/// <param name="Tag">The element's tag name.</param>
/// <param name="Classes">What the author called it, which is how they will recognise it.</param>
/// <param name="Start">When it appears, in seconds.</param>
/// <param name="Duration">How long it stays. <see cref="double.PositiveInfinity"/> means "until the end".</param>
/// <param name="Track">A name for grouping, from <c>data-track</c>. Reporting only.</param>
public sealed record TimelineWindow(
    int Index, string Tag, string? Classes, double Start, double Duration, string? Track)
{
    /// <summary>When it goes, in seconds.</summary>
    public double End => double.IsPositiveInfinity(Duration) ? double.PositiveInfinity : Start + Duration;

    /// <summary>The class CupriCut adds to drive it.</summary>
    public string ClassName => $"cut-t{Index}";

    public string Describe()
    {
        var what = Classes is { Length: > 0 } ? $"<{Tag} class=\"{Classes}\">" : $"<{Tag}>";
        var span = double.IsPositiveInfinity(End)
            ? $"from {Seconds(Start)}s"
            : $"{Seconds(Start)}s to {Seconds(End)}s";
        return Track is { Length: > 0 } ? $"{what} {span} on {Track}" : $"{what} {span}";
    }

    internal static string Seconds(double t) => t.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>What a composition's timeline came to.</summary>
public sealed record TimelinePlan(
    IReadOnlyList<TimelineWindow> Windows,
    double Duration,
    IReadOnlyList<string> Problems)
{
    public bool Any => Windows.Count > 0;

    /// <summary>The distinct tracks, in the order they were first seen.</summary>
    public IReadOnlyList<string> Tracks =>
        [.. Windows.Where(w => w.Track is { Length: > 0 }).Select(w => w.Track!).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// <c>data-start</c>, <c>data-duration</c> and <c>data-track</c>: what is on screen when.
///
/// <para><b>The whole composition is still built once.</b> Nothing is added to or removed from the
/// document per frame - that would mean reloading it, and opening and settling costs 25-370 ms
/// against 3-7 ms for a frame. Instead each timed element gets a generated <c>@keyframes</c> that
/// holds it at <c>opacity: 0</c> outside its window and <c>1</c> inside, with ADJACENT stops so the
/// change is a cut rather than a fade. <c>Animate(t)</c> then does everything, and a composition
/// with a timeline is still pure in <c>t</c> - so it still renders directly rather than being swept.</para>
///
/// <para><b>Why a generated class and not a wrapper.</b> A wrapper element would let the window and
/// the author's own animation coexist, and it was the first design. It also silently changes the
/// layout: the wrapper becomes the flex child, not the element, and a scene inside a flex row stops
/// behaving. The timed element is reserved instead - see below - which costs a stated rule and no
/// surprises.</para>
///
/// <para><b>The rule: a timed element's own <c>animation</c> belongs to CupriCut.</b> The engine
/// runs exactly ONE animation per element - comma-separated lists do nothing, measured in both the
/// shorthand and the longhand forms - so the window and an author's animation on the same element
/// cannot both exist. This is the natural shape anyway: the timed thing is a scene, and the motion
/// belongs to what is inside it. <see cref="Plan"/> reports a violation rather than letting the
/// author's animation disappear quietly.</para>
///
/// <para><b>Giving a late scene its own zero.</b> The engine's clock is absolute and stamps no
/// creation time, so an element that appears at 4s still sees <c>animation-delay</c> measured from
/// 0 - its entrance would have played and finished long before anyone saw it. Each timed element
/// therefore publishes <c>--cut-start</c>, which custom properties inherit into the whole subtree,
/// and a child writes <c>animation-delay: var(--cut-start)</c> to begin when its scene does.
/// <c>calc(var(--cut-start) + 0.2s)</c> would be the obvious next thing to want and does NOT work:
/// <c>calc()</c> is not supported in <c>animation-delay</c> at all, with or without a variable.
/// Stagger inside a scene goes in the keyframe percentages instead.</para>
/// </summary>
public static partial class Timeline
{
    public const string StartAttribute = "data-start";
    public const string DurationAttribute = "data-duration";
    public const string TrackAttribute = "data-track";

    /// <summary>A hair either side of a cut, so the two stops are distinct percentages and the
    /// engine steps between them rather than interpolating. At any sane duration this is far below
    /// one frame.</summary>
    private const double Hair = 0.0001;

    [GeneratedRegex("""<(?<name>[a-zA-Z][^\s/>]*)(?<attrs>(?:"[^"]*"|'[^']*'|[^>"'])*)>""")]
    private static partial Regex OpeningTag();

    [GeneratedRegex("""(?<![-\w])data-(?<which>start|duration|track)\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""",
        RegexOptions.IgnoreCase)]
    private static partial Regex TimingAttr();

    [GeneratedRegex("""(?<![-\w])class\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex ClassAttr();

    /// <summary>Whether the markup mentions the timeline at all, so an ordinary composition pays
    /// nothing for this existing.</summary>
    public static bool Marks(string html) =>
        !string.IsNullOrEmpty(html)
        && (html.Contains(StartAttribute, StringComparison.OrdinalIgnoreCase)
            || html.Contains(DurationAttribute, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Read the timeline out of the markup.
    /// </summary>
    /// <param name="html">The composition's markup.</param>
    /// <param name="css">Its stylesheet, only to check the reserved-animation rule.</param>
    /// <param name="projectDuration">The project's own duration, if it has one. The timeline is
    /// never shorter than this.</param>
    public static TimelinePlan Plan(string html, string? css, double projectDuration = 0)
    {
        if (!Marks(html)) return new TimelinePlan([], Math.Max(0, projectDuration), []);

        var windows = new List<TimelineWindow>();
        var problems = new List<string>();
        var index = 0;

        foreach (Match tag in OpeningTag().Matches(html))
        {
            var attrs = tag.Groups["attrs"].Value;

            double? start = null;
            double? duration = null;
            string? track = null;

            foreach (Match timing in TimingAttr().Matches(attrs))
            {
                var value = timing.Groups["v"].Value.Trim();
                switch (timing.Groups["which"].Value.ToLowerInvariant())
                {
                    case "start": start = Number(value, StartAttribute, problems); break;
                    case "duration": duration = Number(value, DurationAttribute, problems); break;
                    case "track": track = value.Length == 0 ? null : value; break;
                }
            }

            if (start is null && duration is null) continue;

            var classes = ClassAttr().Match(attrs) is { Success: true } c ? c.Groups["v"].Value.Trim() : null;
            var window = new TimelineWindow(
                index++, tag.Groups["name"].Value, classes,
                Math.Max(0, start ?? 0),
                duration is { } d && d > 0 ? d : double.PositiveInfinity,
                track);

            // The one rule, checked rather than assumed to be followed. This is a lint-grade match
            // against the stylesheet text, not a cascade - it catches the case that actually
            // happens (a rule naming the element's class also setting animation) and says so, which
            // is better than the author's animation vanishing with no explanation.
            if (DeclaresAnimation(css, classes) || DeclaresAnimation(attrs, null))
                problems.Add(
                    $"{window.Describe()} carries its own animation, and CupriCut needs that slot for its window - " +
                    "the engine runs one animation per element. Move the motion onto a child.");

            windows.Add(window);
        }

        var duration2 = windows
            .Where(w => !double.IsPositiveInfinity(w.End))
            .Select(w => w.End)
            .DefaultIfEmpty(0)
            .Max();

        return new TimelinePlan(windows, Math.Max(Math.Max(duration2, projectDuration), 0.001), problems);
    }

    /// <summary>
    /// Rewrite a composition so its timeline renders.
    ///
    /// <para>The markup gains a class per timed element; the stylesheet gains a keyframes and a rule
    /// per window. Nothing else is touched, and a composition with no timeline comes back
    /// untouched and unallocated.</para>
    /// </summary>
    public static Composition Apply(Composition composition)
    {
        if (composition.TimelineApplied || !Marks(composition.Html)) return composition;

        var plan = Plan(composition.Html, composition.Css, composition.Defaults?.Duration ?? 0);
        if (!plan.Any) return composition;

        var html = Rewrite(composition.Html, plan);
        var css = (composition.Css ?? string.Empty) + Stylesheet(plan);

        return composition with { Html = html, Css = css, TimelineApplied = true };
    }

    /// <summary>Add the generated class to each timed element's opening tag, in the same order
    /// <see cref="Plan"/> found them.</summary>
    private static string Rewrite(string html, TimelinePlan plan)
    {
        var index = 0;
        return OpeningTag().Replace(html, tag =>
        {
            var attrs = tag.Groups["attrs"].Value;
            if (!TimingAttr().Matches(attrs).Any(m => m.Groups["which"].Value.ToLowerInvariant() is "start" or "duration"))
                return tag.Value;

            var window = plan.Windows[index++];
            var name = tag.Groups["name"].Value;

            var existing = ClassAttr().Match(attrs);
            if (!existing.Success) return $"<{name}{attrs} class=\"{window.ClassName}\">";

            var merged = (existing.Groups["v"].Value.Trim() + " " + window.ClassName).Trim();
            var rewritten = attrs.Remove(existing.Index, existing.Length).Insert(existing.Index, $"class=\"{merged}\"");
            return $"<{name}{rewritten}>";
        });
    }

    /// <summary>The generated stylesheet: one keyframes and one rule per window.</summary>
    private static string Stylesheet(TimelinePlan plan)
    {
        var total = plan.Duration;
        var css = new StringBuilder("\n\n/* ---- CupriCut timeline (generated) ---- */\n");

        foreach (var w in plan.Windows)
        {
            var frames = w.ClassName.Replace("cut-t", "cut-w");

            css.Append($"\n.{w.ClassName} {{ animation: {frames} {Num(total)}s linear both; --cut-start: {Num(w.Start)}s; }}\n");
            css.Append($"@keyframes {frames} {{\n");

            // Percentages of the whole timeline, which is what makes a keyframes position an
            // absolute time. Adjacent stops, so the engine steps rather than fading.
            var open = Percent(w.Start, total);
            var close = double.IsPositiveInfinity(w.End) ? 100d : Percent(w.End, total);

            if (open > 0)
            {
                css.Append("  0% { opacity: 0; }\n");
                css.Append($"  {Num(Math.Max(0, open - Hair))}% {{ opacity: 0; }}\n");
            }
            css.Append($"  {Num(open)}% {{ opacity: 1; }}\n");

            if (close < 100)
            {
                css.Append($"  {Num(close)}% {{ opacity: 1; }}\n");
                css.Append($"  {Num(Math.Min(100, close + Hair))}% {{ opacity: 0; }}\n");
                css.Append("  100% { opacity: 0; }\n");
            }
            else
            {
                css.Append("  100% { opacity: 1; }\n");
            }

            css.Append("}\n");
        }

        return css.ToString();
    }

    private static double Percent(double t, double total) =>
        total <= 0 ? 0 : Math.Clamp(t / total * 100, 0, 100);

    private static string Num(double v) =>
        Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

    private static double? Number(string value, string attribute, List<string> problems)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            return parsed;

        problems.Add($"{attribute}=\"{value}\" is not a number of seconds, so it was ignored.");
        return null;
    }

    /// <summary>Whether some stylesheet text declares an animation for any of these classes. A text
    /// match, deliberately: this is a warning, not a cascade.</summary>
    private static bool DeclaresAnimation(string? css, string? classes)
    {
        if (string.IsNullOrWhiteSpace(css)) return false;

        // No classes named: this is the element's own inline attributes.
        if (classes is null)
            return css.Contains("animation", StringComparison.OrdinalIgnoreCase);

        foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (Match rule in Rules().Matches(css))
            {
                if (!rule.Groups["sel"].Value.Contains("." + name, StringComparison.Ordinal)) continue;
                if (AnimationDecl().IsMatch(rule.Groups["body"].Value)) return true;
            }
        }
        return false;
    }

    [GeneratedRegex(@"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex Rules();

    [GeneratedRegex(@"(?<![-\w])animation(-name)?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex AnimationDecl();
}
