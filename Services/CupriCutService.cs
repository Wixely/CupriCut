using System.Diagnostics;
using CupriCut.Configuration;
using CupriFace;
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
        ContentRoot = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    }

    public CutOptions Options => _options.CurrentValue;

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
        Sweep(LoadComposition(spec.Composition), spec, sink);

    /// <summary>The same sweep over a composition already in hand - which is how a tool reads a
    /// project's render defaults before deciding what to ask for, without loading it twice.</summary>
    public SweepReport Sweep(Composition composition, SweepSpec spec, Action<SweptFrame> sink)
    {
        var options = Options;
        var defaults = composition.Defaults;

        // Precedence, everywhere: what the caller asked for, then what the project remembers, then
        // the server's configured default. A project is a source of defaults, never an override.
        var width = spec.Width > 0 ? spec.Width : defaults?.Width > 0 ? defaults.Width : options.DefaultWidth;
        var height = spec.Height > 0 ? spec.Height : defaults?.Height > 0 ? defaults.Height : options.DefaultHeight;
        var scale = spec.Scale > 0 ? spec.Scale : defaults?.Scale > 0 ? defaults.Scale : 1;
        var fps = spec.SweepFps > 0 ? spec.SweepFps : defaults?.Fps > 0 ? defaults.Fps : options.DefaultFps;
        var alpha = spec.Alpha ?? defaults?.Alpha ?? false;

        if (spec.Times.Count == 0) throw new ArgumentException("A sweep needs at least one time to keep.", nameof(spec));
        if (spec.Times.Any(t => t < 0 || double.IsNaN(t) || double.IsInfinity(t)))
            throw new ArgumentException("Times must be finite and at or after zero.", nameof(spec));

        var pixels = (long)width * scale * height * scale;
        if (pixels > options.MaxPixels)
            throw new CutPolicyException(
                $"{width * scale}x{height * scale} is {pixels:N0} pixels per frame, over Cut:MaxPixels ({options.MaxPixels:N0}).");

        var steps = SweepSteps(spec.Times, fps);
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
            var settled = doc.Settle(width, height, TimeSpan.FromSeconds(options.SettleTimeoutSeconds));
            if (!settled)
                throw new TimeoutException(
                    $"'{composition.Path}' still had {doc.PendingLoads} resource load(s) in flight after {options.SettleTimeoutSeconds}s. " +
                    "A frame rendered before its images arrive is wrong and deterministic, which is worse than wrong and flaky.");

            var info = new SKImageInfo(width * scale, height * scale, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info)
                ?? throw new InvalidOperationException($"Could not create a {width * scale}x{height * scale} render surface.");
            var canvas = surface.Canvas;

            var kept = 0;
            foreach (var t in steps)
            {
                doc.Animate(t);
                canvas.Clear(clear);
                canvas.Save();
                if (scale != 1) canvas.Scale(scale);
                doc.Render(canvas, width, height);
                canvas.Restore();
                canvas.Flush();

                if (!targets.Contains(Key(t))) continue;
                using var image = surface.Snapshot();
                sink(new SweptFrame(kept++, t, image));
            }

            sw.Stop();
            var report = new SweepReport(
                composition.Path, width, height, scale, steps.Length, kept, fps,
                steps[^1], sw.Elapsed.TotalMilliseconds, settled,
                [.. doc.FontReport.Problems.Select(p => $"{p.Family}: {p.Reason}")]);

            _log.LogInformation(
                "Swept {Steps} frames of {Composition} at {Width}x{Height}x{Scale} in {Ms:0}ms, kept {Kept}",
                report.StepsRendered, Path.GetFileName(report.Composition), width, height, scale, report.ElapsedMs, kept);
            return report;
        }
        finally
        {
            _renderGate.Release();
        }
    }

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
