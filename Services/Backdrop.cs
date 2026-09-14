using System.Text.RegularExpressions;

namespace CupriCut.Services;

/// <summary>One element an author marked as the backdrop.</summary>
/// <param name="Tag">The element's tag name, e.g. <c>div</c>.</param>
/// <param name="Label">Whatever the attribute's value was, when it had one - a name for this
/// backdrop, so a report can say which is which.</param>
/// <param name="Classes">The element's classes, which is usually how an author recognises it.</param>
public sealed record BackdropElement(string Tag, string? Label, string? Classes)
{
    public string Describe() =>
        Label is { Length: > 0 } ? $"<{Tag}> \"{Label}\""
        : Classes is { Length: > 0 } ? $"<{Tag} class=\"{Classes}\">"
        : $"<{Tag}>";
}

/// <summary>
/// The backdrop, marked in the markup and turned off per render.
///
/// <para><b>The problem.</b> The same composition is wanted two ways: over its own background for
/// review, where a human has to judge the colours against something, and over nothing for the edit,
/// where the backdrop is whatever the video underneath it is. Authoring that twice means two files
/// that drift. Marking the backdrop instead means one composition and a flag.</para>
///
/// <para><b>Why an inline style and not a stylesheet rule.</b> Measured, not assumed. The engine
/// parses <c>--cupricut-background</c> as an ordinary attribute - it survives into the DOM intact -
/// but a CSS attribute selector on it, <c>[--cupricut-background] { display:none }</c>, matches
/// nothing and fails silently: the backdrop renders as if the rule were not there. An inline
/// <c>style="display:none"</c> works, and beats an author's own <c>display</c> rule on the same
/// element, which is what makes it safe to apply to markup this code did not write.</para>
///
/// <para>The rewrite is a string edit on the opening tag rather than a DOM round trip, because the
/// engine is handed markup as a string and re-serialising someone's HTML to change one attribute
/// risks changing things nobody asked to change.</para>
/// </summary>
public static partial class Backdrop
{
    /// <summary>The attribute that marks an element as the backdrop. The <c>--</c> prefix is the
    /// CSS custom-property convention, borrowed so the marker reads as a CupriCut extension rather
    /// than as HTML someone forgot to remove.</summary>
    public const string Attribute = "--cupricut-background";

    /// <summary>What the rewrite adds, and what it looks for before adding it again.</summary>
    private const string Hide = "display:none";

    // An opening tag, with quoting respected so a > inside an attribute value does not end it.
    [GeneratedRegex("""<(?<name>[a-zA-Z][^\s/>]*)(?<attrs>(?:"[^"]*"|'[^']*'|[^>"'])*)>""")]
    private static partial Regex OpeningTag();

    // The marker as an ATTRIBUTE NAME, with its value if it has one. The leading boundary keeps
    // --cupricut-background-colour, a different attribute, from matching.
    [GeneratedRegex("""(?<![-\w])--cupricut-background(?![-\w])(?:\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+)))?""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    [GeneratedRegex("""(?<![-\w])class\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex ClassAttr();

    [GeneratedRegex("""(?<![-\w])style\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttr();

    /// <summary>Every element in <paramref name="html"/> marked as a backdrop, in document order.
    /// What <c>inspect</c> reports and what the window's checkbox is enabled by.</summary>
    public static IReadOnlyList<BackdropElement> Find(string html)
    {
        if (string.IsNullOrEmpty(html) || !html.Contains("--cupricut-background", StringComparison.OrdinalIgnoreCase))
            return [];

        var found = new List<BackdropElement>();
        foreach (Match tag in OpeningTag().Matches(html))
        {
            var attrs = tag.Groups["attrs"].Value;
            var marker = Marker().Match(attrs);
            if (!marker.Success) continue;

            var label = marker.Groups["v"].Success ? marker.Groups["v"].Value.Trim() : null;
            var classes = ClassAttr().Match(attrs) is { Success: true } c ? c.Groups["v"].Value.Trim() : null;
            found.Add(new BackdropElement(tag.Groups["name"].Value, label, classes));
        }
        return found;
    }

    /// <summary>Whether anything in <paramref name="html"/> is marked at all. A composition with no
    /// backdrop is not an error - the flag simply has nothing to act on, and saying so is more use
    /// than rendering an identical file twice and leaving someone to wonder why.</summary>
    public static bool Marks(string html) => Find(html).Count > 0;

    /// <summary>
    /// <paramref name="html"/> with every marked element hidden.
    ///
    /// <para>Only the opening tag is touched: the subtree is left alone, so an element that comes
    /// back when the flag flips is byte-identical to the one that was never hidden.</para>
    /// </summary>
    public static string Hidden(string html)
    {
        if (!Marks(html)) return html;

        return OpeningTag().Replace(html, tag =>
        {
            var attrs = tag.Groups["attrs"].Value;
            if (!Marker().IsMatch(attrs)) return tag.Value;

            var style = StyleAttr().Match(attrs);
            if (!style.Success) return $"<{tag.Groups["name"].Value}{attrs} style=\"{Hide}\">";

            // Prepending rather than appending would let the author's own later declaration win.
            var existing = style.Groups["v"].Value.Trim();
            if (existing.Contains(Hide, StringComparison.OrdinalIgnoreCase)) return tag.Value;
            var merged = existing.Length == 0 || existing.EndsWith(';') ? existing + Hide : existing + ";" + Hide;
            var rewritten = attrs.Remove(style.Index, style.Length).Insert(style.Index, $"style=\"{merged}\"");
            return $"<{tag.Groups["name"].Value}{rewritten}>";
        });
    }

    /// <summary>
    /// <paramref name="composition"/> rendered with or without its backdrop.
    ///
    /// <para>The flag is resolved the way every other setting is - what the caller asked for, then
    /// what the project remembers, then showing it - and the result is carried on the composition
    /// rather than passed alongside it, so every path that opens a document (the sweep, the
    /// parallel renderer, the window's preview) gets the same markup without being told.</para>
    /// </summary>
    public static Composition Resolve(Composition composition, bool? asked)
    {
        var show = asked ?? composition.Defaults?.ShowBackground ?? true;
        if (show || composition.BackdropHidden) return composition;
        return composition with { Html = Hidden(composition.Html), BackdropHidden = true };
    }
}
