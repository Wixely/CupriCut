namespace CupriCut.Services;

/// <summary>What inlining a document's references found.</summary>
/// <param name="Assets">Inlined bytes, keyed by the source string the markup uses.</param>
/// <param name="Missing">Sources that named nothing on disk. Named rather than stripped: a
/// composition missing its logo renders perfectly and looks wrong, and a broken link somebody can
/// see is worth more than an invisible one.</param>
public sealed record Bundled(
    IReadOnlyDictionary<string, string> Assets,
    IReadOnlyList<string> Missing);

/// <summary>
/// Everything a document refers to, read off the disk beside it and inlined as <c>data:</c> URIs.
///
/// <para><b>Why this is one function.</b> Two importers need it and they must not disagree: a file
/// dragged onto the studio window, and a browser document put through <c>translate</c>. Both turn
/// somebody else's folder into a project, and a project is only worth the name if it is the
/// self-contained file this repository promises - one you can copy, commit or hand to another
/// machine. An import that leaves absolute paths in the markup produces something that renders on
/// exactly one computer, which is the failure this exists to prevent and which
/// <c>translate</c> shipped with until it was measured.</para>
///
/// <para><b>Nothing in the markup is rewritten.</b> A project resolves an asset by looking its
/// source string up in its own dictionary, so inlining <c>logo.png</c> under the key
/// <c>logo.png</c> is the entire mechanism and <c>src="logo.png"</c> keeps working untouched. That
/// is also why the key must be the source <i>exactly as written</i> - see
/// <see cref="CompositionLoader.SourcesOf"/>.</para>
/// </summary>
public static class Bundle
{
    /// <summary>
    /// Inline every relative source in <paramref name="html"/> and <paramref name="css"/> that
    /// exists in <paramref name="directory"/>.
    /// </summary>
    /// <param name="html">The document, unmodified by this call.</param>
    /// <param name="css">Its stylesheet, if it is held apart from the markup.</param>
    /// <param name="directory">The folder the document came from, which is what its relative
    /// sources mean.</param>
    /// <param name="already">Keys the project holds from somewhere other than this directory - a
    /// face fetched from a font service, say. A reference to one of those is neither inlined nor
    /// reported missing: it is already answered, and looking for it beside the document would find
    /// nothing and say so, which is true of the folder and false of the project.</param>
    public static Bundled Beside(string html, string? css, string directory, IReadOnlySet<string>? already = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrEmpty(directory);

        var assets = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var source in CompositionLoader.SourcesOf(html, css))
        {
            if (already?.Contains(source) == true) continue;

            // An absolute path, a data: URI or a URL is the author's own business and is left
            // alone. The engine resolves the first two without help, and inlining the third would
            // mean fetching it - which is a network request, and not one to make because a file
            // happened to mention a hostname.
            if (CompositionLoader.IsResolved(source)) continue;

            var file = Path.GetFullPath(Path.Combine(directory, source.Replace('\\', '/')));
            if (!File.Exists(file))
            {
                missing.Add(source);
                continue;
            }

            assets[source] = DataUri(File.ReadAllBytes(file), CutPackage.MediaTypeOf(file));
        }

        return new Bundled(assets, missing);
    }

    /// <summary>Bytes already in hand, as the <c>data:</c> URI a project stores. For a payload that
    /// never had a path here - a face fetched from a font service, say.</summary>
    public static string DataUri(byte[] bytes, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }
}
