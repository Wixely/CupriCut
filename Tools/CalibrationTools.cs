using System.ComponentModel;
using System.Text.Json;
using CupriCut.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CupriCut.Tools;

/// <summary>
/// What this machine really does, measured rather than claimed.
///
/// <para>Every check here exists because something was assumed and turned out false.
/// <c>libvpx-vp9</c> lists <c>yuva420p</c> among its supported formats, accepts it, reports success
/// and writes <c>yuv420p</c> — so every transparent VP9 render was silently opaque.
/// <c>Environment.ProcessorCount</c> said twelve render workers, and twelve was the slowest setting
/// on the machine, worse than one. Neither was discoverable by reading anything.</para>
/// </summary>
[McpServerToolType]
public static class CalibrationTools
{
    [McpServerTool(Name = "calibrate"),
     Description("""
        Test what this machine's ffmpeg can actually do, and measure the best render parallelism.

        Encodes a two-frame clip per codec and per alpha mode with the real arguments CupriCut
        issues, then probes what came out - because a codec accepting an alpha pixel format is not
        the same as it writing one. Then times a short render at several worker counts to find the
        fastest, which is not the core count and cannot be reasoned about.

        Shows only failures by default; pass all:true for the whole table. Run it after changing
        ffmpeg, or when a render produces something unexpected.
        """)]
    public static string Calibrate(
        CupriCutService cut,
        VideoEncoder encoder,
        [Description("Show every check rather than only the failures.")] bool all = false,
        [Description("Skip the parallelism benchmark, which is the slow part (a few seconds).")] bool skipWorkerBenchmark = false)
    {
        var calibrator = new Calibrator(cut, encoder, cut.RenderLog);
        var report = calibrator.Run(tuneWorkers: !skipWorkerBenchmark);

        return JsonSerializer.Serialize(new
        {
            passed = report.Passed,
            failed = report.Failed,
            allPassed = report.AllPassed,
            elapsedMs = Math.Round(report.ElapsedMs, 0),
            table = report.ToTable(errorsOnly: !all),
            recommendedWorkers = calibrator.BestWorkers,
            configuredWorkers = cut.Options.RenderWorkers == 0
                ? $"0 (using the built-in guess of {ParallelRenderer.DefaultWorkers})"
                : cut.Options.RenderWorkers.ToString(),
            apply = calibrator.BestWorkers is { } best && best != cut.Options.RenderWorkers
                ? $"Set Cut:RenderWorkers to {best} to use it, or pass workers:{best} per render."
                : null,
            checks = (all ? report.Checks : report.Failures).Select(c => new
            {
                c.Group,
                c.Name,
                c.Ok,
                c.Detail,
                c.Fix,
            }),
        }, JsonOpts.Default);
    }
}
