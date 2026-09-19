using System.Diagnostics;
using CupriCut.Configuration;
using CupriFace;
using CupriFace.Components;
using CupriFace.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace CupriCut.Services;

/// <summary>Refused because of a configured limit or root, rather than because the composition is
/// wrong. Tools let this reach the caller with its message intact.</summary>
public sealed class CutPolicyException(string message) : InvalidOperationException(message);

/// <summary>
/// Load, settle, sweep. Every tool and every CLI verb goes through <see cref="Sweep"/> - that is
/// the point of this class, not an accident of layering.
///
/// <para><b>Why one sweep function.</b> A frame is not a pure function of <c>t</c>. Transitions,
/// toasts and reorder easing interpolate from what the previous frame held, so <c>Animate(t)</c>
/// drives them but they carry state between calls. Measured on the engine: a forward sweep and a
/// fresh document at each <c>t</c> agree on 1 frame in 45. Both are perfectly repeatable; they
/// simply do not agree. So "the frame at <c>t</c>" has to be defined, once, for every tool, and the
/// definition is <b>the last frame of a sweep from 0</b> - which is the frame the video will
/// contain. A preview that skips the sweep is a preview that lies.</para>
/// </summary>
public sealed class CupriCutService
{
    private readonly ILogger<CupriCutService> _log;
    private readonly IOptionsMonitor<CutOptions> _options;

    // Renders are CPU-bound and the engine is documented as single-threaded per document, so
    // sweeping one at a time is both the safe reading and the fast one: parallel sweeps would
    // contend for the same cores and finish no sooner. PNG encoding is what goes wide (see
    // FrameEncoder) - it is the 45 ms, not the 5.6.
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    public CupriCutService(IOptionsMonitor<CutOptions> options, ILogger<CupriCutService> log)
    {
        _options = options;
        _log = log;
        ContentRoot = Services.ContentRoot.Locate();
    }

    public CutOptions Options => _options.CurrentValue;

    /// <summary>The logger a helper spun up per render should use, so its output lands with the
    /// service's rather than nowhere.</summary>
    public ILogger RenderLog => _log;

    public string ContentRoot { get; init; }

    // ---- Roots and limits ------------------------------------------------------------------

    /// <summary>Every directory a composition may be read from: the configured roots, plus the
    /// project root, which has to be readable for a saved project to be renderable.</summary>
    public IReadOnlyList<string> CompositionRoots =>
        [.. ConfiguredCompositionRoots.Append(Options.ProjectRoot).Select(Rooted).Where(Directory.Exists).Distinct()];

    private IReadOnlyList<string> ConfiguredCompositionRoots =>
        Options.CompositionRoots.Count > 0 ? Options.CompositionRoots : CutOptions.DefaultCompositionRoots;

    private IReadOnlyList<string> ConfiguredFontDirectories =>
        Options.FontDirectories.Count > 0 ? Options.FontDirectories : CutOptions.DefaultFontDirectories;

    /// <summary>The output directory as an absolute path, created if it is not there.</summary>
    public string OutputRoot
    {
        get
        {
            var root = Rooted(Options.OutputRoot);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>Where <c>.cut.json</c> projects live, created if it is not there. The only place
    /// that is both read and written.</summary>
    public string ProjectRoot
    {
        get
        {
            var root = Rooted(Options.ProjectRoot);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    private string Rooted(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ContentRoot, path));

    /// <summary>Resolve a path a caller gave us to a real file under one of the composition roots,
    /// or refuse. Symlinks are resolved first, so a link inside a root pointing out of it does not
    /// widen the root.</summary>
    public string ResolveRead(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new CutPolicyException("No composition path was given.");
        var roots = CompositionRoots;
        if (roots.Count == 0)
            throw new CutPolicyException("No composition root exists. Set Cut:CompositionRoots to a directory that is there.");

        // A relative path is relative to a root, tried in order - that is what makes
        // "motion/spinner.html" work without the caller knowing where the server keeps them.
        List<string> candidates = Path.IsPathRooted(path)
            ? [Path.GetFullPath(path)]
            : [.. roots.Select(r => Path.GetFullPath(Path.Combine(r, path)))];

        // Two passes on purpose. A relative name is tried against each root in turn, and the roots
        // include the project root, so "hero.cut.json" must not stop at the first root that COULD
        // hold it - it has to find the one that does. The second pass keeps the error message about
        // the roots rather than about a missing file, for a name no root could ever hold.
        var inRoot = candidates.Where(c => roots.Any(r => IsUnder(RealPath(c), r))).ToList();
        foreach (var candidate in inRoot)
        {
            if (File.Exists(candidate)) return candidate;
        }
        if (inRoot.Count > 0) return inRoot[0];

        throw new CutPolicyException(
            $"'{path}' is not under any configured composition root ({string.Join(", ", roots)}). " +
            "Cut:CompositionRoots is the only place compositions, stylesheets, images and fonts are read from.");
    }

    /// <summary>Resolve a caller-supplied output name to a path under the output root, or refuse.
    /// Parent directories are created.</summary>
    public string ResolveWrite(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new CutPolicyException("No output path was given.");
        var root = OutputRoot;
        var full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative));
        if (!IsUnder(full, root))
            throw new CutPolicyException($"'{relative}' resolves outside Cut:OutputRoot ({root}), the only directory a tool may write under.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    private static string RealPath(string path)
    {
        try
        {
            FileSystemInfo info = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
            return Path.GetFullPath(info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)?.FullName ?? info.FullName);
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return p.Equals(r, cmp) || p.StartsWith(r + Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>Resolve a project name to a path under the project root, or refuse. A bare name
    /// gains the <c>.cut.json</c> extension, and anything that is not a <c>.cut.json</c> is
    /// refused - the project root is read-write, so what may land in it is narrow on purpose.</summary>
    public string ResolveProject(string name, bool forWriting)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new CutPolicyException("No project name was given.");

        // A bare name gains the extension, because "hero" is what an agent will send. A name that
        // already carries a DIFFERENT extension is refused rather than silently renamed: someone
        // asking to write "notes.txt" meant a text file, and this is not the place for one.
        var extension = Path.GetExtension(name);
        if (!CutProject.IsProjectPath(name) && extension.Length > 0)
            throw new CutPolicyException(
                $"'{name}' is not a {CutProject.Extension} file. Only projects are read from and written to the project root.");
        var withExtension = CutProject.IsProjectPath(name) ? name : name.TrimEnd('.') + CutProject.Extension;
        var root = ProjectRoot;
        var full = Path.GetFullPath(Path.IsPathRooted(withExtension) ? withExtension : Path.Combine(root, withExtension));

        if (!IsUnder(full, root))
            throw new CutPolicyException($"'{name}' resolves outside Cut:ProjectRoot ({root}), the only directory projects are read from and written to.");
        if (!CutProject.IsProjectPath(full))
            throw new CutPolicyException($"'{name}' is not a {CutProject.Extension} file. Only projects may be written to the project root.");

        if (forWriting) Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        else if (!File.Exists(full)) throw new FileNotFoundException($"No project named '{name}' under {root}.", full);
        return full;
    }

    /// <summary>Read a project back whole - the call a second run makes when it has nothing but the
    /// name.</summary>
    public CutProject LoadProject(string name) =>
        CutProject.Parse(File.ReadAllText(ResolveProject(name, forWriting: false)), name);

    /// <summary>Write a project, stamping when and by which engine.</summary>
    public string SaveProject(string name, CutProject project)
    {
        var path = ResolveProject(name, forWriting: true);
        project.Meta.Updated = DateTimeOffset.UtcNow;
        project.Meta.Engine ??= EngineVersion;
        File.WriteAllText(path, project.ToJson());
        _log.LogInformation("Saved project {Path} ({Bytes} bytes)", path, new FileInfo(path).Length);
        return path;
    }

    /// <summary>Every project under the project root.</summary>
    public IReadOnlyList<string> ListProjects()
    {
        var root = ProjectRoot;
        return [.. Directory.EnumerateFiles(root, "*" + CutProject.Extension, SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The folders projects are organised into, as relative paths with <c>/</c> separators.
    ///
    /// <para>Derived from where the projects actually are rather than kept as a list somewhere: a
    /// folder IS a directory under the project root, so there is nothing to get out of step. An
    /// empty directory is included too, because a folder someone made and has not filled yet is
    /// still a folder they made.</para>
    /// </summary>
    public IReadOnlyList<string> ListFolders()
    {
        var root = ProjectRoot;
        if (!Directory.Exists(root)) return [];

        return [.. Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Create a folder under the project root.
    /// </summary>
    /// <returns>Its relative path, which is what everything else refers to it by.</returns>
    public string CreateFolder(string relative)
    {
        var folder = FolderPath(relative);
        Directory.CreateDirectory(folder);
        return Path.GetRelativePath(ProjectRoot, folder).Replace('\\', '/');
    }

    /// <summary>
    /// Move a project to another name or folder.
    ///
    /// <para>Both ends go through <see cref="ResolveProject"/>, so neither can leave the project
    /// root and neither can be something that is not a project. Refuses to overwrite: two projects
    /// with the same name is a thing to be told about, not to resolve by destroying one.</para>
    /// </summary>
    /// <param name="from">The project as it is now.</param>
    /// <param name="to">Where it should be - a folder, or a folder and a new name.</param>
    /// <returns>Its new relative path.</returns>
    public string MoveProject(string from, string to)
    {
        var source = ResolveProject(from, forWriting: false);

        // "promos" means "into promos, keeping the name". Only a target that names a project is
        // taken as a rename, so moving is the common case and needs no repetition of the name.
        var target = CutProject.IsProjectPath(to)
            ? to
            : CombineRelative(to, Path.GetFileName(source));

        var destination = ResolveProject(target, forWriting: true);

        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return Path.GetRelativePath(ProjectRoot, destination).Replace('\\', '/');

        if (File.Exists(destination))
            throw new CutPolicyException(
                $"There is already a project at '{target}'. Rename one of them - moving over it would destroy work that is not yours to destroy.");

        lock (_projectLock)
        {
            File.Move(source, destination);
        }

        _log.LogInformation("Moved project {From} to {To}", from, target);
        return Path.GetRelativePath(ProjectRoot, destination).Replace('\\', '/');
    }

    /// <summary>A folder under the project root, checked the way a project path is.</summary>
    private string FolderPath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return ProjectRoot;
        if (CutProject.IsProjectPath(relative))
            throw new CutPolicyException($"'{relative}' names a project, not a folder.");

        var root = ProjectRoot;
        var full = Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(root, relative));
        if (!IsUnder(full, root))
            throw new CutPolicyException($"'{relative}' resolves outside Cut:ProjectRoot ({root}).");
        return full;
    }

    private static string CombineRelative(string folder, string name) =>
        string.IsNullOrWhiteSpace(folder) ? name : folder.Replace('\\', '/').TrimEnd('/') + "/" + name;

    /// <summary>Read, change, write - the shape every annotation edit needs, done in one place so a
    /// concurrent GUI edit and MCP edit cannot interleave a lost update.</summary>
    public CutProject EditProject(string name, Action<CutProject> edit)
    {
        lock (_projectLock)
        {
            var project = LoadProject(name);
            edit(project);
            SaveProject(name, project);
            return project;
        }
    }

    // The window and the MCP server are two faces on this one service, and both write annotations.
    private readonly Lock _projectLock = new();

    public static string EngineVersion => $"CupriFace {typeof(CupriDocument).Assembly.GetName().Version}";

    public void EnsureVideoAllowed()
    {
        if (!Options.EnableVideo)
            throw new CutPolicyException("Video is disabled. Set Cut:EnableVideo to true to allow ffmpeg encoding; PNG output stays available either way.");
    }

    // ---- Loading ---------------------------------------------------------------------------

    public Composition LoadComposition(string path) => CompositionLoader.Load(path, ResolveRead);

    /// <summary>A document with the composition's fonts registered and the strict policy set.
    /// Never settled and never animated - <see cref="Sweep"/> owns that order.</summary>
    public CupriDocument OpenDocument(Composition composition)
    {
        var doc = CupriDocument.Load(composition.Html, composition.Css);
        try
        {
            // Without a registry every cupri-* element expands to nothing: it lays out, paints
            // NOTHING, settles cleanly and lints clean. Measured - an 80x80 <cupri-image> with a
            // data: URI painted 0 pixels without this line and exactly 6400 with it.
            //
            // The studio wired its own registry from the start, so the window's controls worked
            // and nobody noticed that COMPOSITIONS had none. Meanwhile `lint` was telling authors
            // to replace <img> (CF0030) with <cupri-image>, which is right, and which then drew
            // nothing at all - the one substitution the tool actively recommends.
            doc.UseComponents(ComponentRegistry.Default());

            foreach (var dir in ConfiguredFontDirectories.Select(Rooted).Where(Directory.Exists))
                doc.LoadFonts(dir, recursive: true);

            // Always. A family that would resolve to whatever this machine has installed is an
            // error naming the family, not a silent substitution - that is the whole difference
            // between a render a CI runner reproduces and one it merely resembles.
            doc.FontPolicy = FontPolicy.RegisteredOnly;
            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    // ---- The sweep -------------------------------------------------------------------------

    /// <summary>
    /// Render <paramref name="spec"/>, handing every requested frame to <paramref name="sink"/> in
    /// time order.
    ///
    /// <para>Every step between 0 and the last requested time is rendered, whether or not it was
    /// asked for, because that is what carries transition state forward. The frames that were asked
    /// for are snapshotted out of the surface the whole sweep reuses.</para>
    /// </summary>
    public SweepReport Sweep(SweepSpec spec, Action<SweptFrame> sink) =>
        Sweep(null, spec, sink);

    /// <summary>The same sweep over a composition already in hand - which is how a tool reads a
    /// project's render defaults before deciding what to ask for, without loading it twice.</summary>
    public SweepReport Sweep(Composition? compositionOrNull, SweepSpec spec, Action<SweptFrame> sink)
    {
        var options = Options;
        var defaults = (compositionOrNull ?? LoadedComposition(null, spec)).Defaults;

        // Precedence, everywhere: what the caller asked for, then what the project remembers, then
        // the server's configured default. A project is a source of defaults, never an override.
        // The geometry is worked out in ONE place - see Presentation - because the encoder has to
        // agree with this exactly, and it used to derive the same numbers for itself.
        var present = Presentation.Resolve(spec, defaults, options);
        var width = present.LogicalWidth;
        var height = present.LogicalHeight;
        var fps = spec.SweepFps > 0 ? spec.SweepFps : defaults?.Fps > 0 ? defaults.Fps : options.DefaultFps;
        var alpha = spec.Alpha ?? defaults?.Alpha ?? false;

        if (spec.Times.Count == 0) throw new ArgumentException("A sweep needs at least one time to keep.", nameof(spec));
        if (spec.Times.Any(t => t < 0 || double.IsNaN(t) || double.IsInfinity(t)))
            throw new ArgumentException("Times must be finite and at or after zero.", nameof(spec));

        if (present.Pixels > options.MaxPixels)
            throw new CutPolicyException(
                $"{present.OutputWidth}x{present.OutputHeight} is {present.Pixels:N0} pixels per frame, over Cut:MaxPixels ({options.MaxPixels:N0}).");

        // The founding measurement was that a frame depends on the frames before it - but only for a
        // composition that carries state between Animate calls. @keyframes does not, and measured on
        // this repository's worked compositions a direct render is byte-identical to a swept one and
        // 10-45x faster. So sweep when it is needed and not otherwise.
        // Before the purity analysis and before anything opens a document: hiding the backdrop is
        // a change to the markup, and everything downstream should be looking at one composition.
        // Both rewrites happen before the purity analysis and before anything opens a document, so
        // everything downstream sees one composition. The timeline's generated animations are
        // @keyframes like any other, so a composition with a timeline is still pure in t.
        var composition = Timeline.Apply(
            Backdrop.Resolve(LoadedComposition(compositionOrNull, spec), spec.ShowBackground));
        var purity = spec.ForceSweep
            ? new PurityVerdict(false, ["the caller asked for a full sweep"])
            : Purity.Analyse(composition);

        var steps = purity.PureInTime
            ? [.. spec.Times.Select(Snap).Distinct().Order()]
            : SweepSteps(spec.Times, fps);

        // Zero means no ceiling, which is the default: long clips are a supported thing to want.
        if (options.MaxFrames > 0 && steps.Length > options.MaxFrames)
            throw new CutPolicyException(
                $"Reaching t={spec.Times.Max():0.###}s at {fps:0.##} fps means sweeping {steps.Length:N0} frames, over Cut:MaxFrames ({options.MaxFrames:N0}). " +
                "A frame is rendered by rendering every frame before it, so this is the real cost.");

        var targets = new HashSet<long>(spec.Times.Select(Key));
        var clear = alpha
            ? SKColors.Transparent
            : spec.Background ?? ParseBackground(defaults?.Background) ?? SKColors.White;

        _renderGate.Wait();
        try
        {
            using var doc = OpenDocument(composition);
            var sw = Stopwatch.StartNew();

            // Frame 0's clock before anything is fetched: Settle renders, and a render is what asks
            // for an image in the first place.
            doc.Animate(0);
            var settled = doc.Settle((int)Math.Ceiling(width), (int)Math.Ceiling(height),
                TimeSpan.FromSeconds(options.SettleTimeoutSeconds));
            if (!settled)
                throw new TimeoutException(
                    $"'{composition.Path}' still had {doc.PendingLoads} resource load(s) in flight after {options.SettleTimeoutSeconds}s. " +
                    "A frame rendered before its images arrive is wrong and deterministic, which is worse than wrong and flaky.");

            var info = new SKImageInfo(present.OutputWidth, present.OutputHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info)
                ?? throw new InvalidOperationException($"Could not create a {present.OutputWidth}x{present.OutputHeight} render surface.");
            var canvas = surface.Canvas;

            var kept = 0;
            foreach (var t in steps)
            {
                doc.Animate(t);
                canvas.Clear(clear);
                present.Apply(canvas);
                doc.Render(canvas, width, height);
                canvas.Restore();
                canvas.Flush();

                if (!targets.Contains(Key(t))) continue;
                using var image = surface.Snapshot();
                sink(new SweptFrame(kept++, t, image));
            }

            sw.Stop();
            var report = new SweepReport(
                composition.Path, steps.Length, kept, fps,
                steps[^1], sw.Elapsed.TotalMilliseconds, settled,
                [.. doc.FontReport.Problems.Select(p => $"{p.Family}: {p.Reason}")])
            { Purity = purity, Present = present };

            _log.LogInformation(
                "{Mode} {Steps} frames of {Composition} at {Geometry} in {Ms:0}ms, kept {Kept}",
                purity.PureInTime ? "Rendered" : "Swept",
                report.StepsRendered, Path.GetFileName(report.Composition), present.Describe(), report.ElapsedMs, kept);
            return report;
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>The composition a spec names, unless the caller already has it.</summary>
    private Composition LoadedComposition(Composition? given, SweepSpec spec) =>
        given ?? LoadComposition(spec.Composition);

    /// <summary>Every time the sweep steps through: one per frame at <paramref name="fps"/> from 0
    /// to the last requested time, plus any requested time that does not land on a step. Requested
    /// times are rendered exactly rather than snapped, so <c>t=1.234</c> means 1.234.</summary>
    public static double[] SweepSteps(IReadOnlyList<double> times, double fps)
    {
        var last = times.Max();
        var set = new SortedSet<double>();
        var count = (int)Math.Floor(last * fps + 1e-9);
        for (var i = 0; i <= count; i++) set.Add(Snap(i / fps));
        foreach (var t in times) set.Add(Snap(t));
        return [.. set];
    }

    private static SKColor? ParseBackground(string? value) =>
        !string.IsNullOrWhiteSpace(value) && SKColor.TryParse(value, out var colour) ? colour : null;

    // Times are compared and de-duplicated at microsecond resolution: i/fps is not exact in binary
    // and a caller's 1.5 must be the same frame as the sweep's 45/30.
    private static double Snap(double t) => Math.Round(t, 6, MidpointRounding.AwayFromZero);

    private static long Key(double t) => (long)Math.Round(t * 1_000_000, MidpointRounding.AwayFromZero);
}
