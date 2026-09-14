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

        apply:true writes the measured worker count to CupriCut.Local.json - the per-machine
        configuration layer - and it takes effect immediately and on every later run. This is the
        one thing here that writes CupriCut's own configuration rather than the output root, and it
        writes exactly one integer that was measured on this machine. Without it the number is
        reported and nothing changes.
        """)]
    public static string Calibrate(
        CupriCutService cut,
        VideoEncoder encoder,
        [Description("Show every check rather than only the failures.")] bool all = false,
        [Description("Skip the parallelism benchmark, which is the slow part (a few seconds).")] bool skipWorkerBenchmark = false,
        [Description("Save the measured worker count so it is used from now on. Writes one key to CupriCut.Local.json and takes effect without a restart.")] bool apply = false)
    {
        var calibrator = new Calibrator(cut, encoder, cut.RenderLog);
        var report = calibrator.Run(tuneWorkers: !skipWorkerBenchmark);

        string? saved = null;
        if (apply && calibrator.BestWorkers is { } measured)
        {
            saved = LocalSettings.SaveRenderWorkers(cut.ContentRoot, measured);
            // In memory as well: the config file watcher is debounced, and a render started in the
            // next few hundred milliseconds should already use the measured number.
            cut.Options.RenderWorkers = measured;
        }

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
            saved,
            apply = saved is not null || calibrator.BestWorkers is not { } best || best == cut.Options.RenderWorkers
                ? null
                : $"Call calibrate again with apply:true to use {best} from now on, or pass workers:{best} per render.",
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
