using CupriFace;
using CupriFace.Diagnostics;
using CupriFace.Text;

namespace CupriCut.Services;

/// <summary>How much a finding matters.</summary>
public enum FindingLevel
{
    /// <summary>Worth knowing. Nothing is wrong.</summary>
    Info,

    /// <summary>It will render, and it may not render the same somewhere else.</summary>
    Warning,

    /// <summary>It will not render what the author meant.</summary>
    Error,
}

/// <summary>One thing worth saying about a composition.</summary>
/// <param name="Level">How much it matters.</param>
/// <param name="Code">A stable identifier. <c>CF*</c> comes from the engine's own reader,
/// <c>CUT*</c> from CupriCut.</param>
/// <param name="What">What is wrong, in one sentence.</param>
/// <param name="Fix">What to do about it, when there is something to do.</param>
/// <param name="Line">Where, when the source knows.</param>
public sealed record Finding(FindingLevel Level, string Code, string What, string? Fix = null, int Line = 0);

/// <summary>Everything one look at a composition turns up.</summary>
public sealed record Examination(
    string Composition,
    int Width,
    int Height,
    double Fps,
    double Duration,
    TimelinePlan Timeline,
    EventPlan Events,
    IReadOnlyList<BackdropElement> Backdrops,
    IReadOnlyList<string> Stylesheets,
    IReadOnlyList<string> References,
    IReadOnlyList<string> RegisteredFamilies,
    IReadOnlyList<FontUse> Fonts,
    bool Settled,
    int PendingLoads,
    PurityVerdict Purity,
    IReadOnlyList<Finding> Findings)
{
    public int Errors => Findings.Count(f => f.Level == FindingLevel.Error);

    public int Warnings => Findings.Count(f => f.Level == FindingLevel.Warning);

    /// <summary>The one-word answer. What a CI step gates on.</summary>
    public string Verdict => Errors > 0 ? "errors" : Warnings > 0 ? "warnings" : "clean";
}

/// <summary>One family a composition asked for, and what answered.</summary>
/// <param name="Asked">The family named in the CSS.</param>
/// <param name="Answered">What the engine resolved it to.</param>
/// <param name="Source">Where that face came from.</param>
/// <param name="MachineDependent">True when the answer came from the machine rather than from a
/// registered file - which is the difference between a render CI reproduces and one it resembles.</param>
public sealed record FontUse(string Asked, int Weight, string Slant, string? Answered, string Source, bool MachineDependent);

/// <summary>
/// One look at a composition: what it is, and whether it will render the same somewhere else.
///
/// <para>Both <c>inspect</c> and <c>lint</c> come from here, because they are the same work asked
/// two ways - one wants the structure, the other wants the verdict, and neither should open the
/// document twice or drift from the other about what it found.</para>
///
/// <para>A composition has to be RENDERED once before it can be examined. A layout is what asks for
/// a font family, so nothing resolves until something paints; a resource has not failed to load
/// until settling has been given its chance. Reading the markup alone would report an empty font
/// table and call it deterministic.</para>
/// </summary>
public static partial class Inspector
{
    /// <summary>A family the engine resolved from the machine rather than from a registered file.</summary>
    public const string MachineFont = "CUT001";

    /// <summary>A resource that never arrived.</summary>
    public const string Unsettled = "CUT002";

    /// <summary>Something the timeline could not make sense of.</summary>
    public const string TimelineProblem = "CUT003";

    /// <summary>Not pure in <c>t</c> - correct, and slower. Informational.</summary>
    public const string Impure = "CUT004";

    /// <summary>A font the composition asked for that nothing could answer.</summary>
    public const string MissingFont = "CUT005";

    /// <summary>A declared event that cannot be read, or that nothing will ever reach.</summary>
    public const string EventProblem = "CUT007";

    /// <summary>Stored cues that no longer describe the track they were read from.</summary>
    public const string StaleAudio = "CUT008";

    public static Examination Examine(CupriCutService cut, string path)
    {
        var loaded = Timeline.Apply(cut.LoadComposition(path));
        var defaults = loaded.Defaults;
        var options = cut.Options;

        var width = defaults?.Width > 0 ? defaults.Width : options.DefaultWidth;
        var height = defaults?.Height > 0 ? defaults.Height : options.DefaultHeight;
        var fps = defaults?.Fps > 0 ? defaults.Fps : options.DefaultFps;

        var timeline = loaded.Timeline;
        var duration = timeline.Any ? timeline.Duration : defaults?.Duration ?? 0;
        var events = Events.Plan(loaded.Html);

        using var doc = cut.OpenDocument(loaded);
        doc.Animate(0);
        var settled = doc.Settle(width, height, TimeSpan.FromSeconds(options.SettleTimeoutSeconds));

        // A layout is what asks for a family, so nothing resolves until something paints.
        using (doc.RenderToImage(width, height)) { }

        var report = doc.FontReport;
        var fonts = report.Resolutions
            .Select(r => new FontUse(
                r.Family, r.Weight, r.Slant.ToString(), r.ResolvedFamily,
                r.Source.ToString(), r.Source != FontSource.Registered))
            .ToList();

        var fontProblems = report.Problems.Select(p => $"'{p.Family}': {p.Reason}").ToList();
        var findings = Collect(loaded, timeline, events, duration, fontProblems, fonts, settled,
            doc.PendingLoads, width, height, out var purity);

        return new Examination(
            loaded.Path, width, height, fps, duration, timeline, events,
            loaded.Backdrops, loaded.Stylesheets, [.. loaded.References.Distinct()],
            report.RegisteredFamilies, fonts, settled, doc.PendingLoads, purity, findings);
    }

    /// <summary>Whether a project's stored cues still describe the track it carries.
    ///
    /// <para>Only checkable when the track was embedded: cues kept without one are not wrong, just
    /// unverifiable, and warning about them would be warning about a choice rather than a
    /// mistake.</para></summary>
    private static IEnumerable<Finding> StaleCues(CutProject? project)
    {
        if (project?.Audio is not { Asset: { Length: > 0 } key } audio) yield break;
        if (!project.Assets.TryGetValue(key, out var uri)) yield break;

        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) yield break;

        byte[] bytes;
        try { bytes = Convert.FromBase64String(uri[(comma + 1)..]); }
        catch (FormatException) { yield break; }

        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (string.Equals(actual, audio.Sha256, StringComparison.OrdinalIgnoreCase)) yield break;

        yield return new Finding(FindingLevel.Warning, StaleAudio,
            $"The cues stored for '{audio.Source}' were read from a different file than the '{key}' "
            + "this project carries now - the track was replaced after it was analysed.",
            "Run attach_audio again to re-read the cues, or put back the track they were timed to.");
    }

    private static List<Finding> Collect(
        Composition loaded, TimelinePlan timeline, EventPlan events, double duration,
        IReadOnlyList<string> fontProblems, IReadOnlyList<FontUse> fonts, bool settled, int pending,
        int width, int height, out PurityVerdict purity)
    {
        var findings = new List<Finding>();

        // The engine's own reader first. It knows things about its own layout that nothing here
        // could work out - an element that produced no render output, a box with no area holding
        // visible content, a property it silently ignored.
        // "" and not null. Through 0.25.0 a null stylesheet turned the CSS checks off entirely,
        // inline <style> included, and this tool read nothing but markup while looking like it was
        // working - CupriFace#183, fixed in 0.25.1 and re-measured on the upgrade (both forms now
        // report the same two warnings). Kept explicit anyway: "no stylesheet" and "check nothing"
        // should never have been the same argument, and this says which one is meant.
        // At the composition's OWN frame, not the doctor's 1024x768 default. The overflow checks
        // are about content running past the viewport, so asking about the wrong viewport reports a
        // 1280-wide composition as broken for being 1280 wide.
        foreach (var f in Doctor.Check(loaded.Html, loaded.Css ?? string.Empty, width, height))
        {
            findings.Add(new Finding(
                f.Severity switch
                {
                    Severity.Error => FindingLevel.Error,
                    Severity.Warning => FindingLevel.Warning,
                    _ => FindingLevel.Info,
                },
                f.Code, f.Message, string.IsNullOrWhiteSpace(f.Fix) ? null : f.Fix, f.Line));
        }

        // A family answered by the machine is the difference between a render a CI runner
        // reproduces and one it merely resembles. This is the whole reason FontPolicy is
        // RegisteredOnly, and it is still worth reporting when one slips through.
        foreach (var font in fonts.Where(f => f.MachineDependent))
            findings.Add(new Finding(FindingLevel.Warning, MachineFont,
                $"'{font.Asked}' was answered by {font.Answered ?? "the machine"} ({font.Source}), not by a registered file.",
                "Put the face in a Cut:FontDirectories folder, or name a family that is registered."));

        foreach (var problem in fontProblems)
            findings.Add(new Finding(FindingLevel.Error, MissingFont, problem,
                "Add the file to a font directory, or use a family that resolved."));

        if (!settled)
            findings.Add(new Finding(FindingLevel.Error, Unsettled,
                $"{pending} resource load(s) were still in flight after settling.",
                "A frame rendered before its images arrive is wrong and deterministic, which is worse than wrong and flaky. Check the paths."));

        foreach (var problem in timeline.Problems)
            findings.Add(new Finding(FindingLevel.Warning, TimelineProblem, problem));

        // A comment between the stops of a @keyframes block silently changes the animation's
        // values - two of them moved a bar's final width from 545px to 714px, and nothing anywhere
        // said so. CupriFace#184,
        // and it is worth a warning precisely because commenting your keyframes is normal practice.
        // The cues are stored so they need not be recomputed; the hash is stored so that storing
        // them cannot quietly become a lie. Replacing the track without re-reading it would
        // otherwise be found out by an animation that no longer lands on anything, which is the
        // worst possible moment to find it out.
        foreach (var stale in StaleCues(loaded.Project)) findings.Add(stale);

        foreach (var problem in events.Problems)
            findings.Add(new Finding(FindingLevel.Warning, EventProblem, problem));

        // Not an error - a clip is often a window onto a longer composition, and an event outside
        // that window is a perfectly sensible thing to have written. But it is also exactly what a
        // typo looks like, and nothing else would ever mention it.
        foreach (var e in events.PastTheEnd(duration))
            findings.Add(new Finding(FindingLevel.Warning, EventProblem,
                $"'{e.Name}' is at {TimelineWindow.Seconds(e.At)}s, past the end of a "
                + $"{TimelineWindow.Seconds(duration)}s composition, so nothing will reach it.",
                "Move the event, or give the composition a duration that covers it."));

        purity = Purity.Analyse(loaded);
        if (!purity.PureInTime)
            findings.Add(new Finding(FindingLevel.Info, Impure,
                "Not pure in t, so a frame is reached by sweeping every frame before it. " + purity.Summary,
                "A composition with no transitions, toasts or scroll-driven easing is sampled at any t directly, which is 10-45x faster."));

        return findings;
    }
}
