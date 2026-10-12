namespace CupriCut.Services;

/// <summary>What a dropped file turned out to be.</summary>
public enum DropKind
{
    /// <summary>Nothing CupriCut takes.</summary>
    Refused,

    /// <summary>An <c>.html</c> composition, imported as a new project.</summary>
    Composition,

    /// <summary>A <c>.cut.json</c> or <c>.cutpkg</c>, copied in.</summary>
    Project,

    /// <summary>An image or a font, inlined into a project that was already open.</summary>
    Asset,
}

/// <summary>
/// What became of one dropped file.
/// </summary>
/// <param name="Kind">What it was taken for.</param>
/// <param name="Name">The project it became, or the one it was added to. Empty when refused.
///
/// <para>A path relative to the project root, <b>with its extension</b>, which is how
/// <see cref="CupriCutService.ListProjects"/> names things and therefore how the window and every
/// tool address them. Spelled out because the bare stem also works for saving and does NOT work
/// for opening - a drop that handed the window "hero" imported the file and then failed to preview
/// it, which is how this came to be written down.</para></param>
/// <param name="Path">Where that project now lives. Empty when refused.</param>
/// <param name="Message">One line, for the status bar. Says what happened, and for a refusal says
/// what would have worked instead.</param>
/// <param name="Inlined">Assets carried in beside the document, by the name the markup uses.</param>
/// <param name="Missing">Sources the document refers to that were not next to it. Named rather
/// than silently dropped: a composition missing its logo renders perfectly and looks wrong, and
/// the fix is to drag the logo in too.</param>
public sealed record DropResult(
    DropKind Kind,
    string Name,
    string Path,
    string Message,
    IReadOnlyList<string> Inlined,
    IReadOnlyList<string> Missing)
{
    public bool Accepted => Kind != DropKind.Refused;

    internal static DropResult Refuse(string message) =>
        new(DropKind.Refused, string.Empty, string.Empty, message, [], []);
}

/// <summary>
/// A file dragged onto the studio window, turned into something CupriCut owns.
///
/// <para><b>Why this is a service and not window code.</b> Everything interesting about a drop is
/// the classification and the import, and neither needs a window - the engine's own
/// <c>DropDriver</c> scripts the gesture, so the whole path is testable headlessly. The window's
/// share is one handler that calls this and puts the message in the status bar.</para>
///
/// <para><b>The read roots, and why this one is allowed outside them.</b> Every other way into
/// this codebase goes through <see cref="CupriCutService.ResolveRead"/>, because every other way
/// in is a request arriving over a socket from an agent, and the roots are what bound what an
/// agent can reach. A drop is not that. It is a person at this machine pointing at their own file,
/// in a window they opened, and refusing it for being outside a configured directory would be
/// refusing the only thing the gesture can possibly mean. The engine says the same thing about
/// trust from the other side: a dropped file is <c>ResourceTrust.LocalFile</c> - the user pointed
/// at it, but the app never chose it.</para>
///
/// <para><b>What that does not widen.</b> The write surface is untouched: this writes projects,
/// into the project root, through <see cref="CupriCutService.SaveProject"/>, exactly as
/// <c>save_project</c> does. Nothing here lets an agent read a file it could not read before -
/// there is no tool for it, on purpose, and the MCP server cannot raise a drop. What a drop
/// produces is a project, and a project is already something every tool was allowed to read.</para>
///
/// <para><b>Assets come with it.</b> A dropped composition's images and fonts are inlined as
/// <c>data:</c> URIs under the names its own markup uses, which is what makes the import a real
/// import rather than a copy that only works in the folder it came from. Nothing in the markup is
/// rewritten - a project resolves an asset by looking its source string up in the dictionary, so
/// <c>src="logo.png"</c> keeps working by name.</para>
/// </summary>
public static class Dropped
{
    /// <summary>Extensions a drop will inline into the open project. Images and fonts, which is
    /// what a composition actually refers to; audio is deliberately absent, because a track has to
    /// be analysed for its cues rather than merely carried, and that is what the audio verb is
    /// for.</summary>
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico",
        ".ttf", ".otf", ".woff", ".woff2",
    };

    /// <summary>
    /// Take one dropped file.
    /// </summary>
    /// <param name="cut">The service, for the project root and the save path.</param>
    /// <param name="path">The file's absolute path, as the OS reported it.</param>
    /// <param name="folder">The folder column it landed on, relative to the project root. Empty
    /// for the top level, null when the drop was not on a folder at all.</param>
    /// <param name="openProject">The project currently open in the window, if any. An image can
    /// only be attached to something, and this is the something.</param>
    public static DropResult Accept(CupriCutService cut, string path, string? folder = null, string? openProject = null)
    {
        ArgumentNullException.ThrowIfNull(cut);
        if (string.IsNullOrWhiteSpace(path))
            return DropResult.Refuse("That drop carried no path. A browser withholds one; this window is not a browser, so it is a bug if you see this.");

        // Dragging a folder onto a window is an ordinary thing to do, and a folder has no bytes.
        // Checked rather than discovered by catching the read.
        if (Directory.Exists(path))
            return DropResult.Refuse(
                $"'{System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))}' is a folder. "
                + "Drop the composition inside it, or use the Projects page to make a folder here.");

        if (!File.Exists(path))
            return DropResult.Refuse($"'{System.IO.Path.GetFileName(path)}' is not there any more.");

        var extension = System.IO.Path.GetExtension(path);

        // A failure becomes a refusal with its own message rather than an exception. Two reasons,
        // and the second is the one that matters: a drop has no confirmation step to report back
        // through, and a gesture carrying four files must not lose the other three because the
        // second one was unreadable.
        try
        {
            if (CutProject.IsProjectPath(path))
                return TakeProject(cut, path, folder);

            if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                return TakeComposition(cut, path, folder);

            if (AssetExtensions.Contains(extension))
                return TakeAsset(cut, path, openProject);
        }
        catch (Exception ex)
        {
            return DropResult.Refuse($"Could not take {System.IO.Path.GetFileName(path)}: {ex.Message}");
        }

        return DropResult.Refuse(
            $"CupriCut does not take {(extension.Length == 0 ? "a file with no extension" : extension)}. "
            + "Drop an .html composition or a .cut.json / .cutpkg project to open it, or an image or "
            + "font to add it to the project that is open.");
    }

    /// <summary>Every dropped file in one gesture, in the order the OS listed them.</summary>
    public static IReadOnlyList<DropResult> AcceptAll(
        CupriCutService cut, IEnumerable<string> paths, string? folder = null, string? openProject = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return [.. paths.Select(p => Accept(cut, p, folder, openProject))];
    }

    // ---- the three things it takes ----------------------------------------------------------

    /// <summary>An existing project, copied in under the project root.</summary>
    private static DropResult TakeProject(CupriCutService cut, string path, string? folder)
    {
        // Already ours: opening it is the whole job, and copying it would leave two of them.
        var inRoot = IsUnder(path, cut.ProjectRoot);
        var name = NameFor(path, folder, inRoot ? null : cut);

        if (inRoot)
        {
            var relative = System.IO.Path.GetRelativePath(cut.ProjectRoot, path).Replace('\\', '/');
            return new DropResult(DropKind.Project, relative, path,
                $"{System.IO.Path.GetFileName(path)} is already in the project root - opened it.", [], []);
        }

        // Read it the way every other reader does, so a package and a plain file both arrive as the
        // same object and this does not become the one place that only knows about half the format.
        var project = CutPackage.LooksLikePackage(path)
            ? CutPackage.Read(path, path)
            : CutProject.Parse(File.ReadAllText(path), path);

        var saved = cut.SaveProject(name, project);
        return new DropResult(DropKind.Project, Relative(cut, saved), saved,
            $"Imported {System.IO.Path.GetFileName(path)} as {name}.", [], []);
    }

    /// <summary>An HTML composition, imported as a project with its stylesheets and assets.</summary>
    private static DropResult TakeComposition(CupriCutService cut, string path, string? folder)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".";
        var raw = File.ReadAllText(path);

        // Link tags pulled in and removed - the engine ignores them, so a composition that kept
        // them would render unstyled. Everything else in the markup is left exactly as written.
        var (html, css, missingSheets) = CompositionLoader.Detach(raw, directory);

        // The same inlining `translate` does, because they are the same job: somebody else's folder
        // becoming a project that can leave it. See Services/Bundle.cs.
        var bundled = Bundle.Beside(html, css, directory);
        var assets = bundled.Assets;
        var missing = new List<string>(missingSheets.Select(s => s + " (stylesheet)"));
        missing.AddRange(bundled.Missing);

        var name = NameFor(path, folder, cut);
        var project = new CutProject
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Description = $"Imported from {System.IO.Path.GetFileName(path)}.",
            Html = html,
            Css = css,
            Assets = new Dictionary<string, string>(assets, StringComparer.Ordinal),
        };

        project.Meta.Engine = CupriCutService.EngineVersion;
        project.Meta.Notes.Add($"Dropped onto the studio window from {directory}.");
        if (assets.Count > 0)
            project.Meta.Notes.Add($"Inlined {assets.Count} asset(s) from beside the source: {string.Join(", ", assets.Keys)}.");
        if (missing.Count > 0)
            project.Meta.Notes.Add(
                $"{missing.Count} source(s) were not next to the document and are NOT in this project: "
                + string.Join(", ", missing)
                + ". The markup still refers to them by name, so dropping each one on the window will fill it in.");
        // Said in the file, because the person who drops a browser block and the person who
        // wonders why it does not move are often the same person a week apart.
        if (raw.Contains("<script", StringComparison.OrdinalIgnoreCase))
            project.Meta.Notes.Add(
                "The source carried <script>. There is no JavaScript engine here, so anything it "
                + "animated does not move. Run `translate` on the original to compile a GSAP "
                + "timeline into @keyframes instead of importing it as-is.");

        var saved = cut.SaveProject(name, project);

        var said = $"Imported {System.IO.Path.GetFileName(path)} as {name}";
        if (assets.Count > 0) said += $" with {assets.Count} asset(s)";
        said += missing.Count > 0 ? $"; {missing.Count} source(s) missing - see the notes." : ".";

        return new DropResult(DropKind.Composition, Relative(cut, saved), saved, said, [.. assets.Keys], missing);
    }

    /// <summary>An image or a font, inlined into the project that is open.</summary>
    private static DropResult TakeAsset(CupriCutService cut, string path, string? openProject)
    {
        if (string.IsNullOrWhiteSpace(openProject))
            return DropResult.Refuse(
                $"Nothing is open, so there is nowhere to put {System.IO.Path.GetFileName(path)}. "
                + "Open a project first, then drop the image on it.");

        var key = System.IO.Path.GetFileName(path);
        var bytes = File.ReadAllBytes(path);
        var uri = $"data:{CutPackage.MediaTypeOf(path)};base64,{Convert.ToBase64String(bytes)}";

        // Through EditProject, so a drop and an MCP edit cannot interleave into a lost update -
        // the window and the server are two faces on one service and both write projects.
        var project = cut.EditProject(openProject, p => p.Assets[key] = uri);
        var saved = cut.ResolveProject(openProject, forWriting: false);

        var how = Hint(path);
        return new DropResult(DropKind.Asset, openProject, saved,
            $"Added {key} to {project.Name}. {how}", [key], []);
    }

    /// <summary>What to write in the composition to use what was just added. Worth saying at the
    /// moment of the drop: an asset nobody refers to is bytes in a file doing nothing.</summary>
    private static string Hint(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ttf" or ".otf" or ".woff" or ".woff2" =>
                $"Use it with @font-face {{ font-family:'Name'; src:url('{System.IO.Path.GetFileName(path)}'); }} "
                + "- a registered face, not a machine substitution.",
            _ => $"Refer to it as <cupri-image src=\"{System.IO.Path.GetFileName(path)}\"> or url('{System.IO.Path.GetFileName(path)}').",
        };

    // ---- naming ----------------------------------------------------------------------------

    /// <summary>
    /// The project name a dropped file becomes: its own name, in the folder it landed on, with a
    /// number appended rather than overwriting something that is already there.
    ///
    /// <para>Never silently replacing an existing project is the point. A drop is one gesture with
    /// no confirmation step, and the obvious way to lose an afternoon's work is to drag in a file
    /// whose name matches something already saved. <paramref name="cut"/> null skips the check,
    /// for the one caller that is not writing.</para>
    /// </summary>
    private static string NameFor(string path, string? folder, CupriCutService? cut)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        // A .cut.json has two extensions, so one strip leaves "hero.cut".
        if (stem.EndsWith(".cut", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];

        var prefix = string.IsNullOrWhiteSpace(folder) ? "" : folder.Replace('\\', '/').TrimEnd('/') + "/";
        if (cut is null) return prefix + stem;

        var candidate = prefix + stem;
        for (var n = 2; Exists(cut, candidate); n++) candidate = $"{prefix}{stem}-{n}";
        return candidate;
    }

    /// <summary>Whether a project of this name is already saved, in either shape.
    ///
    /// <para>Asked of the listing rather than of <c>ResolveProject</c>, which refuses a name that
    /// is not there by throwing - the right answer for a reader, and the wrong shape for a
    /// question about existence.</para></summary>
    private static bool Exists(CupriCutService cut, string name)
    {
        var json = name + CutProject.Extension;
        var package = name + CutPackage.Extension;
        return cut.ListProjects().Any(p =>
            p.Equals(json, StringComparison.OrdinalIgnoreCase)
            || p.Equals(package, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A saved project's path as everything else in the codebase names it: relative to the
    /// project root, with forward slashes, extension kept.</summary>
    private static string Relative(CupriCutService cut, string saved) =>
        System.IO.Path.GetRelativePath(cut.ProjectRoot, saved).Replace('\\', '/');

    private static bool IsUnder(string path, string root)
    {
        var full = System.IO.Path.GetFullPath(path);
        var under = System.IO.Path.GetFullPath(root);
        if (!under.EndsWith(System.IO.Path.DirectorySeparatorChar))
            under += System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(under, StringComparison.OrdinalIgnoreCase);
    }
}
