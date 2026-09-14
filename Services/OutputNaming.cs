using System.Globalization;

namespace CupriCut.Services;

/// <summary>
/// What a rendered file is called.
///
/// <para><b>The problem.</b> An export of two formats wrote <c>mp4.mp4</c> and <c>mask.mp4</c> into
/// a directory named after the composition. Nothing said what was in them, and the next export
/// silently replaced them - so the only way to keep two takes was to rename the first by hand, and
/// the only way to tell them apart afterwards was to remember. A render is a take, and takes
/// accumulate.</para>
///
/// <para><b>The rule.</b> A name the caller gave is used exactly as given; a name CupriCut chooses
/// carries the project and the moment. That distinction matters: a build step wants
/// <c>hero.mp4</c> to be <c>hero.mp4</c> every time, and a person iterating wants yesterday's take
/// still to be there. Defaults are for people; explicit names are for scripts.</para>
///
/// <para>The stamp is <c>yyyyMMdd-HHmmss</c> - sortable as text, legal on every filesystem (no
/// colons, which Windows refuses), and readable without decoding. Local time rather than UTC,
/// because it is read by whoever is sitting in front of it.</para>
/// </summary>
public static class OutputNaming
{
    public const string StampFormat = "yyyyMMdd-HHmmss";

    /// <summary>Formats exactly the moment it is given, in the offset it is given. A pure function
    /// of its argument - converting to local time in here would make every name depend on the
    /// machine's timezone and nothing testable.</summary>
    public static string Stamp(DateTimeOffset when) =>
        when.ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>Now, in local time. The one place that decides "local", because it is the one place
    /// with no argument to respect.</summary>
    public static string Stamp() => Stamp(DateTimeOffset.Now);

    /// <summary>
    /// The name for one file CupriCut chose itself: the project, the moment, and what it is.
    /// </summary>
    /// <param name="stem">The project or composition name, already made safe.</param>
    /// <param name="extension">Including the dot.</param>
    /// <param name="what">An optional discriminator - the format, when one directory holds several.</param>
    /// <param name="when">Defaults to now.</param>
    public static string File(string stem, string extension, string? what = null, DateTimeOffset? when = null)
    {
        var stamp = when is { } at ? Stamp(at) : Stamp();
        return what is { Length: > 0 } ? $"{stem}_{stamp}_{what}{extension}" : $"{stem}_{stamp}{extension}";
    }

    /// <summary>
    /// The directory for one run that produces several files - an export, a frame sequence.
    ///
    /// <para>The moment goes on the FOLDER rather than on every file inside it: opening it should
    /// show <c>hero_mp4.mp4</c> and <c>hero_mask.mp4</c>, not the same timestamp four times.</para>
    /// </summary>
    public static string Run(string stem, DateTimeOffset? when = null) =>
        $"{stem}_{(when is { } at ? Stamp(at) : Stamp())}";

    /// <summary>A file INSIDE a stamped run directory: the folder already says when.</summary>
    public static string InRun(string stem, string what, string extension) =>
        $"{stem}_{what}{extension}";

    /// <summary>Whether the caller named the output themselves. An explicit name is honoured
    /// exactly - no stamp, no suffix - because something is probably expecting that name.</summary>
    public static bool Explicit(string? given) => !string.IsNullOrWhiteSpace(given);
}
