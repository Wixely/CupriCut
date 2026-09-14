using System.Text;
using System.Text.RegularExpressions;

namespace CupriCut.Services;

/// <summary>A composition made loadable: the HTML, the CSS gathered from every
/// <c>&lt;link rel="stylesheet"&gt;</c> or carried by a project, and every relative source in
/// either resolved - to an absolute path for a file composition, or to an inlined <c>data:</c> URI
/// for a project's own asset.</summary>
public sealed record Composition(
    string Path,
    string Directory,
    string Html,
    string? Css,
    IReadOnlyList<string> Stylesheets,
    IReadOnlyList<string> References)
{
    /// <summary>Present when this came from a <c>.cut.json</c>. Its render block supplies whatever
    /// the caller left out - never overriding anything the caller gave.</summary>
    public CutProject? Project { get; init; }

    /// <summary>Whether <see cref="Backdrop.Hidden"/> has already rewritten <see cref="Html"/>.
    /// Carried rather than re-detected so a composition that passes through two resolution points
    /// - the encoder's and the sweep's - is not edited twice.</summary>
    public bool BackdropHidden { get; init; }

    /// <summary>Elements the author marked as the backdrop. Read off the ORIGINAL markup, so this
    /// still lists them once they are hidden.</summary>
    public IReadOnlyList<BackdropElement> Backdrops => Backdrop.Find(Html);

    public RenderSettings? Defaults => Project?.Render;
}

/// <summary>
/// Turns a composition file into something <c>CupriDocument.Load</c> can take.
///
/// <para>Two jobs the engine deliberately does not do. It reads <c>&lt;style&gt;</c> and an external
/// stylesheet handed to <c>Load</c>, but not <c>&lt;link rel="stylesheet"&gt;</c> - a document is
/// given its CSS by its host, and here the host is this. And a bare <c>src</c> or <c>url()</c>
/// resolves through <c>SourceResolver</c> to a file path relative to the <b>process</b> working
/// directory, which for a server is wherever it was started. An author writing
/// <c>&lt;cupri-image src="logo.png"&gt;</c> means the logo next to the HTML, so every relative
/// source is resolved before the engine sees it.</para>
/// </summary>
public static partial class CompositionLoader
{
    [GeneratedRegex("""<link\b[^>]*\brel\s*=\s*["']?stylesheet["']?[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex("""\bhref\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefAttr();

    [GeneratedRegex("""\b(src|poster)\s*=\s*(?:"([^"]*)"|'([^']*)')""", RegexOptions.IgnoreCase)]
    private static partial Regex SrcAttr();

    [GeneratedRegex("""url\(\s*(?:"([^"]*)"|'([^']*)'|([^)'"]+))\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    /// <summary>Read <paramref name="path"/> - an <c>.html</c> composition or a <c>.cut.json</c>
    /// project - and everything it points at. <paramref name="allow"/> gates each referenced file,
    /// so a composition cannot pull in a stylesheet from outside the configured roots.</summary>
    public static Composition Load(string path, Func<string, string> allow)
    {
        var full = allow(path);
        if (!File.Exists(full)) throw new FileNotFoundException($"No composition at '{path}'.", full);
        return CutProject.IsProjectPath(full)
            ? FromProject(CutProject.Parse(File.ReadAllText(full), full), full)
            : FromHtml(full, allow);
    }

    /// <summary>A project is a composition whose assets travel with it. Nothing is read off disk:
    /// an asset the HTML names is replaced by the <c>data:</c> URI the project carries.</summary>
    public static Composition FromProject(CutProject project, string path)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        var references = new List<string>();

        string? Resolve(string source) =>
            project.Assets.TryGetValue(source, out var uri) ? uri : null;

        var html = RewriteHtml(project.Html, Resolve, references);
        var css = project.Css is null ? null : RewriteCss(project.Css, Resolve, references);

        return new Composition(System.IO.Path.GetFullPath(path), directory, html, css, [], references)
        {
            Project = project,
        };
    }

    private static Composition FromHtml(string full, Func<string, string> allow)
    {
        var directory = System.IO.Path.GetDirectoryName(full)!;
        var html = File.ReadAllText(full);
        var references = new List<string>();
        var sheets = new List<string>();
        var css = new StringBuilder();

        string? Resolve(string source) => Absolute(directory, source);

        // <link rel="stylesheet"> - pulled in and then removed, since the engine would ignore it.
        html = LinkTag().Replace(html, m =>
        {
            var href = FirstGroup(HrefAttr().Match(m.Value));
            if (string.IsNullOrWhiteSpace(href) || IsResolved(href)) return m.Value;
            var sheet = allow(System.IO.Path.Combine(directory, href));
            if (!File.Exists(sheet)) throw new FileNotFoundException($"Stylesheet '{href}' referenced by the composition does not exist.", sheet);
            sheets.Add(sheet);
            var sheetDirectory = System.IO.Path.GetDirectoryName(sheet)!;
            css.AppendLine(RewriteCss(File.ReadAllText(sheet), s => Absolute(sheetDirectory, s), references));
            return string.Empty;
        });

        html = RewriteHtml(html, Resolve, references);
        return new Composition(full, directory, html, css.Length == 0 ? null : css.ToString(), sheets, references);
    }

    /// <summary>Already something the engine can resolve without knowing where the composition
    /// lives.</summary>
    public static bool IsResolved(string src) =>
        src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
        src.StartsWith('#') ||
        System.IO.Path.IsPathRooted(src);

    private static string RewriteHtml(string html, Func<string, string?> resolve, List<string> references)
    {
        // Inline <style> blocks carry url() of their own - @font-face above all.
        html = ReplaceStyleBlocks(html, block => RewriteCss(block, resolve, references));
        return SrcAttr().Replace(html, m =>
        {
            var value = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (string.IsNullOrWhiteSpace(value) || IsResolved(value)) return m.Value;
            if (resolve(value) is not { } resolved) return m.Value;
            references.Add(value);
            return $"{m.Groups[1].Value}=\"{resolved}\"";
        });
    }

    private static string RewriteCss(string css, Func<string, string?> resolve, List<string> references) =>
        CssUrl().Replace(css, m =>
        {
            var value = FirstGroup(m);
            if (string.IsNullOrWhiteSpace(value) || IsResolved(value)) return m.Value;
            if (resolve(value) is not { } resolved) return m.Value;
            references.Add(value);
            return $"url('{resolved}')";
        });

    private static string ReplaceStyleBlocks(string html, Func<string, string> rewrite)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (true)
        {
            var open = html.IndexOf("<style", i, StringComparison.OrdinalIgnoreCase);
            if (open < 0) break;
            var openEnd = html.IndexOf('>', open);
            if (openEnd < 0) break;
            var close = html.IndexOf("</style", openEnd, StringComparison.OrdinalIgnoreCase);
            if (close < 0) break;
            sb.Append(html, i, openEnd + 1 - i);
            sb.Append(rewrite(html[(openEnd + 1)..close]));
            i = close;
        }
        sb.Append(html, i, html.Length - i);
        return sb.ToString();
    }

    // Forward slashes even on Windows: the value goes back into CSS and HTML, where a backslash is
    // an escape character rather than a separator.
    private static string Absolute(string directory, string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, relative.Replace('\\', '/'))).Replace('\\', '/');

    private static string FirstGroup(Match m)
    {
        for (var g = 1; g < m.Groups.Count; g++)
            if (m.Groups[g].Success) return m.Groups[g].Value;
        return string.Empty;
    }
}
