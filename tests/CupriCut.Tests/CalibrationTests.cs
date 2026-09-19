using CupriCut.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// Calibration exists because two things were assumed and both were false: that a codec accepting
/// an alpha pixel format writes one, and that the processor count is the right amount of
/// parallelism. These cover the reporting and the worker resolution; the encoder checks themselves
/// need a real ffmpeg and are exercised by running the verb.
/// </summary>
public sealed class CalibrationTests(ITestOutputHelper output)
{
    [Fact]
    public void The_default_view_is_the_failures_and_says_how_to_see_the_rest()
    {
        var report = new CalibrationReport(
        [
            new CalibrationCheck("Codecs", "h264", true, "yuv420p"),
            new CalibrationCheck("Embedded alpha", "vp9", false, "DROPPED alpha - wrote yuv420p", "Use prores, or a matte."),
        ], 1200);

        var table = report.ToTable(errorsOnly: true);

        Assert.Contains("vp9", table);
        Assert.Contains("DROPPED", table);
        Assert.Contains("Use prores", table);       // the fix travels with the failure
        Assert.DoesNotContain("h264", table);       // a passing check is noise here
        Assert.Contains("1 of 2", table);
    }

    [Fact]
    public void Everything_passing_says_so_in_one_line_rather_than_a_wall_of_ticks()
    {
        var report = new CalibrationReport(
        [
            new CalibrationCheck("Codecs", "h264", true, "yuv420p"),
            new CalibrationCheck("Codecs", "prores", true, "yuva444p10le"),
        ], 900);

        Assert.True(report.AllPassed);
        Assert.Equal("All 2 checks passed in 900 ms.", report.ToTable(errorsOnly: true));
        Assert.Contains("prores", report.ToTable(errorsOnly: false));
    }

    /// <summary>Counts this machine can actually give, so the theory below is about PRECEDENCE
    /// and not about how many cores the developer happens to have.
    ///
    /// <para>It was written with 4 and 8 in it, which passed on a 12-core laptop and failed on
    /// CI's 4-core runner: <c>Resolve</c> clamps to <see cref="Environment.ProcessorCount"/> -
    /// correctly, and there is a separate test for that - so a "calibrated 8" came back as 4 and
    /// the rule under test was never reached. The first time this suite ever ran anywhere but
    /// the machine it was written on, it said so.</para></summary>
    public static TheoryData<int, int, int> Precedence()
    {
        var all = Environment.ProcessorCount;
        var some = Math.Max(1, all / 2);

        return new TheoryData<int, int, int>
        {
            // asked for, configured, expected
            { some, 0, some },      // an explicit request wins
            { some, all, some },    // ...even over a calibrated value
            { 0, all, all },        // calibration beats the built-in guess
            { 0, 0, -1 },           // nothing set: the guess
        };
    }

    [Theory]
    [MemberData(nameof(Precedence))]
    public void Workers_come_from_the_caller_then_calibration_then_the_guess(int requested, int configured, int expected)
    {
        var resolved = ParallelRenderer.Resolve(requested, configured);
        Assert.Equal(expected < 0 ? ParallelRenderer.DefaultWorkers : expected, resolved);
    }

    [Fact]
    public void Workers_are_never_more_than_the_machine_has()
    {
        Assert.Equal(Environment.ProcessorCount, ParallelRenderer.Resolve(9999, 0));
        Assert.Equal(Environment.ProcessorCount, ParallelRenderer.Resolve(0, 9999));
        Assert.InRange(ParallelRenderer.DefaultWorkers, 1, Environment.ProcessorCount);
    }

    [Fact]
    public void The_worker_benchmark_measures_something_and_picks_the_fastest()
    {
        // The real thing, small: it must produce timings and choose the lowest ms/frame.
        using var harness = new Harness(o => { o.DefaultWidth = 320; o.DefaultHeight = 180; });
        var calibrator = new Calibrator(harness.Cut, harness.Encoder, NullLogger.Instance);

        var timings = calibrator.TuneWorkers();

        Assert.NotEmpty(timings);
        foreach (var t in timings)
        {
            output.WriteLine($"{t.Workers,3} workers  {t.MsPerFrame:0.00} ms/frame  {t.Fps:0} fps");
            Assert.True(t.MsPerFrame > 0, "a timing of zero means nothing was rendered");
        }

        Assert.Contains(timings, t => t.Workers == 1);
        Assert.Equal(timings.Min(t => t.MsPerFrame), timings.MinBy(t => t.MsPerFrame)!.MsPerFrame);
    }
}
