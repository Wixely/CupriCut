using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CupriCut.Services;

/// <summary>
/// Finds out what this machine actually does, rather than what it claims.
///
/// <para><b>Why this exists.</b> Every capability here was at some point assumed and then found to
/// be false. <c>libvpx-vp9</c> lists <c>yuva420p</c> among its supported pixel formats, accepts it,
/// reports success — and writes <c>yuv420p</c>, so every transparent VP9 render was silently
/// opaque. <c>Environment.ProcessorCount</c> said twelve workers and twelve workers was the slowest
/// setting on the machine, worse than one. Neither was discoverable by reading documentation; both
/// took two frames and a stopwatch.</para>
///
/// <para>So: encode a tiny clip for each thing that matters, probe what came out, and time the
/// render at several worker counts. It costs a few seconds and replaces a guess with a number.</para>
/// </summary>
public sealed class Calibrator(CupriCutService cut, VideoEncoder encoder, ILogger log)
{
    /// <summary>The composition the worker tuning renders. Deliberately its own rather than the
    /// user's: a calibration that depends on which project happens to be open measures the project,
    /// not the machine. Gradients, text and a transform are what an ordinary composition costs.</summary>
    private const string Probe = """
        <div class="s"><div class="c">Calibration</div><div class="r"></div></div>
        <style>
          .s { width:100%; height:100%; font-family:"Noto Sans";
               background:linear-gradient(140deg,#10131a,#1d2432); }
          .c { width:600px; margin:120px 0 0 90px; font-size:56px; font-weight:700; color:#f4f6fb;
               animation:m 2s linear infinite; }
          .r { width:420px; height:64px; margin:40px 0 0 90px; border-radius:12px;
               background:linear-gradient(90deg,#d9642a,#e8a04a); animation:g 2s ease-in-out infinite alternate; }
          @keyframes m { from { transform:translateX(0); } to { transform:translateX(180px); } }
          @keyframes g { from { transform:scale(0.7); } to { transform:scale(1); } }
        </style>
        """;

    /// <summary>Run every check. <paramref name="tuneWorkers"/> adds the render benchmark, which is
    /// the slow part — a few seconds against a fraction of one for the encoder checks.</summary>
    public CalibrationReport Run(bool tuneWorkers = true, CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();
        var checks = new List<CalibrationCheck>();

        checks.AddRange(Tools());
        if (checks.Any(c => c.Group == "Tools" && c.Name == "ffmpeg" && !c.Ok))
        {
            // Nothing below can mean anything without an encoder.
            sw.Stop();
            return new CalibrationReport(checks, sw.Elapsed.TotalMilliseconds);
        }

        checks.AddRange(CodecChecks(token));
        checks.AddRange(AlphaChecks(token));
        if (tuneWorkers) checks.AddRange(WorkerChecks(token));

        sw.Stop();
        var report = new CalibrationReport(checks, sw.Elapsed.TotalMilliseconds);
        log.LogInformation("Calibration: {Passed} passed, {Failed} failed in {Ms:0}ms",
            report.Passed, report.Failed, report.ElapsedMs);
        return report;
    }

    /// <summary>The best worker count measured, or null when the benchmark did not run.</summary>
    public int? BestWorkers { get; private set; }

    // ---- the checks --------------------------------------------------------------------------

    private IEnumerable<CalibrationCheck> Tools()
    {
        var ffmpeg = encoder.Probe();
        yield return new CalibrationCheck("Tools", "ffmpeg", ffmpeg.Available,
            ffmpeg.Available ? ffmpeg.Version ?? "present" : ffmpeg.Error ?? "not found",
            ffmpeg.Available ? null : "Install ffmpeg, or set Cut:FfmpegPath to it. The Docker image carries one.");

        var probeOk = encoder.FfprobeVersion() is { Length: > 0 };
        yield return new CalibrationCheck("Tools", "ffprobe", probeOk,
            probeOk ? encoder.FfprobeVersion()! : "not found beside ffmpeg",
            probeOk ? null : "Without it a finished file's pixel format cannot be checked, so a dropped alpha channel goes unnoticed.");
    }

    private IEnumerable<CalibrationCheck> CodecChecks(CancellationToken token)
    {
        foreach (var codec in VideoEncoder.Codecs.Values)
        {
            token.ThrowIfCancellationRequested();
            var (ok, detail) = encoder.TryTinyEncode(codec, alpha: false, AlphaMode.Embedded, out _);
            yield return new CalibrationCheck("Codecs", codec.Name, ok,
                ok ? detail : detail,
                ok ? null : $"This ffmpeg cannot encode {codec.Name} ({codec.Encoder}). Use one of the codecs that passed.");
        }
    }

    private IEnumerable<CalibrationCheck> AlphaChecks(CancellationToken token)
    {
        // Embedded alpha, per codec that claims to have it. This is the check that exists because
        // the claim and the result disagreed.
        foreach (var codec in VideoEncoder.Codecs.Values.Where(c => c.Alpha))
        {
            token.ThrowIfCancellationRequested();
            var (ok, detail) = encoder.TryTinyEncode(codec, alpha: true, AlphaMode.Embedded, out var format);
            var carried = ok && format is { Length: > 0 } && VideoEncoder.HasAlpha(format);
            yield return new CalibrationCheck("Embedded alpha", codec.Name, carried,
                carried ? $"kept alpha ({format})"
                        : ok ? $"DROPPED alpha - wrote {format}" : detail,
                carried ? null : "This build accepts the alpha pixel format and does not write it. Use a codec that passed, or alphaMode matteBelow / matteRight.");
        }

        // A matte needs no codec alpha at all; it needs the filter graph to exist.
        foreach (var mode in new[] { AlphaMode.MatteBelow, AlphaMode.MatteRight })
        {
            token.ThrowIfCancellationRequested();
            var (ok, detail) = encoder.TryTinyEncode(VideoEncoder.Codecs["h264"], alpha: true, mode, out var format);
            yield return new CalibrationCheck("Alpha matte", $"h264 {mode}", ok,
                ok ? $"packed into an opaque frame ({format})" : detail,
                ok ? null : "The split/alphaextract/stack filter graph failed. Transparency on h264 depends on it.");
        }
    }

    private IEnumerable<CalibrationCheck> WorkerChecks(CancellationToken token)
    {
        var results = TuneWorkers(token);
        if (results.Count == 0)
        {
            yield return new CalibrationCheck("Parallelism", "benchmark", false,
                "could not run", "Rendering could not be timed; the default worker count stands.");
            yield break;
        }

        var best = results.MinBy(r => r.MsPerFrame)!;
        BestWorkers = best.Workers;
        var single = results.FirstOrDefault(r => r.Workers == 1);
        var speedup = single is null ? 0 : single.MsPerFrame / best.MsPerFrame;

        foreach (var result in results)
        {
            var isBest = result.Workers == best.Workers;
            yield return new CalibrationCheck("Parallelism", $"{result.Workers} worker(s)", true,
                $"{result.MsPerFrame:0.00} ms/frame, {1000 / result.MsPerFrame:0} fps{(isBest ? "   <- best" : "")}");
        }

        yield return new CalibrationCheck("Parallelism", "recommended", true,
            $"{best.Workers} workers ({speedup:0.0}x over one; the built-in default is {ParallelRenderer.DefaultWorkers})",
            best.Workers == ParallelRenderer.DefaultWorkers
                ? null
                : $"The built-in guess of {ParallelRenderer.DefaultWorkers} is not this machine's best. " +
                  $"Apply it in the window, or run cupricut calibrate --apply, and {best.Workers} is saved and used from then on.");
    }

    /// <summary>Time the renderer at a spread of worker counts.</summary>
    public IReadOnlyList<WorkerTiming> TuneWorkers(CancellationToken token = default)
    {
        var results = new List<WorkerTiming>();

        string composition;
        try
        {
            composition = WriteProbe();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not stage the calibration composition");
            return results;
        }

        try
        {
            var loaded = cut.LoadComposition(composition);
            var times = Enumerable.Range(0, 96).Select(i => i / 60.0).ToArray();
            var spec = new SweepSpec
            {
                Composition = composition,
                Width = cut.Options.DefaultWidth,
                Height = cut.Options.DefaultHeight,
                Times = times,
                SweepFps = 60,
            };

            var renderer = new ParallelRenderer(cut, log);

            // Warm the JIT and the font cache, or the first count measured wears the cost.
            renderer.RenderInOrder(loaded, spec, times[..12], 2, (_, _) => { }, token);

            foreach (var workers in Candidates())
            {
                token.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                renderer.RenderInOrder(loaded, spec, times, workers, (_, _) => { }, token);
                sw.Stop();
                results.Add(new WorkerTiming(workers, sw.Elapsed.TotalMilliseconds / times.Length));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Worker calibration failed");
        }
        finally
        {
            TryDelete(composition);
        }

        return results;
    }

    /// <summary>Worker counts worth trying: every small one, then thinning out, up to the logical
    /// core count. No point measuring 11 and 12 separately when the interesting cliff is between
    /// the physical count and twice it.</summary>
    private static IEnumerable<int> Candidates()
    {
        var max = Environment.ProcessorCount;
        var seen = new HashSet<int>();
        foreach (var n in new[] { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32 })
        {
            if (n > max) break;
            if (seen.Add(n)) yield return n;
        }
        if (seen.Add(max)) yield return max;
    }

    /// <summary>The probe composition has to be readable through the ordinary roots, because that
    /// is the only path the renderer has. It is written into the project root, which is the one
    /// place writing is allowed, and removed afterwards.</summary>
    private string WriteProbe()
    {
        var name = $"calibration-{Guid.NewGuid():N}"[..24] + ".html";
        var path = Path.Combine(cut.ProjectRoot, name);
        File.WriteAllText(path, Probe);
        return path;
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { log.LogDebug(ex, "Could not remove the calibration composition {Path}", path); }
    }
}

/// <summary>One measured worker count.</summary>
public sealed record WorkerTiming(int Workers, double MsPerFrame)
{
    public double Fps => 1000 / Math.Max(0.0001, MsPerFrame);
}
