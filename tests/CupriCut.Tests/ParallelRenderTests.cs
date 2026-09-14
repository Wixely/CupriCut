using System.Diagnostics;
using CupriCut.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

public sealed class ParallelRenderTests(ITestOutputHelper output)
{
    [Fact]
    public void How_fast_is_the_renderer_alone()
    {
        // One test, one process, one warm-up - so the numbers are comparable with each other
        // rather than with whatever else the test host happened to be running.
        foreach (var workers in new[] { 1, 2, 3, 4, 6, 8, 12 }) Measure(workers);
    }

    private void Measure(int workers)
    {
        using var harness = new Harness();
        var root = FindRepoRoot();
        var name = harness.WriteComposition("timebase.html",
            File.ReadAllText(Path.Combine(root, "compositions", "timebase.html")));
        var composition = harness.Cut.LoadComposition(name);

        var times = Enumerable.Range(0, 480).Select(i => i / 120.0).ToArray();
        var spec = new SweepSpec { Composition = name, Width = 1280, Height = 720, Times = times, SweepFps = 120 };
        var renderer = new ParallelRenderer(harness.Cut, NullLogger.Instance);

        // Warm the process, then measure with the frames simply discarded - no encoder involved.
        renderer.RenderInOrder(composition, spec, times[..24], workers, (_, _) => { });

        var sw = Stopwatch.StartNew();
        var seen = 0;
        var report = renderer.RenderInOrder(composition, spec, times, workers, (i, _) =>
        {
            Assert.Equal(seen, i);      // strictly in order, which is what the pipe requires
            seen++;
        });
        sw.Stop();

        output.WriteLine($"{workers,2} worker(s): {times.Length} frames in {sw.ElapsedMilliseconds,5} ms " +
                         $"= {sw.Elapsed.TotalMilliseconds / times.Length:0.00} ms/frame, {times.Length / sw.Elapsed.TotalSeconds:0} fps");

        Assert.Equal(times.Length, seen);
        Assert.Equal(times.Length, report.Frames);
    }

    [Fact]
    public void Frames_arrive_in_order_and_match_the_sequential_render()
    {
        // The claim sharding rests on: out-of-order rendering, in-order delivery, same pixels.
        using var harness = new Harness();
        var name = harness.WriteComposition("keyframed.html", Harness.Keyframed);
        var composition = harness.Cut.LoadComposition(name);

        var times = Enumerable.Range(0, 60).Select(i => i / 30.0).ToArray();
        var spec = new SweepSpec { Composition = name, Width = 160, Height = 80, Times = times, SweepFps = 30 };

        var parallel = new Dictionary<int, byte[]>();
        new ParallelRenderer(harness.Cut, NullLogger.Instance)
            .RenderInOrder(composition, spec, times, 8, (i, span) => parallel[i] = span.ToArray());

        var sequential = new Dictionary<int, byte[]>();
        harness.Cut.Sweep(composition, spec, frame =>
        {
            using var bitmap = FrameEncoder.Copy(frame.Image);
            sequential[frame.Index] = bitmap.GetPixelSpan().ToArray();
        });

        Assert.Equal(times.Length, parallel.Count);
        foreach (var (index, pixels) in sequential)
            Assert.True(pixels.AsSpan().SequenceEqual(parallel[index]), $"frame {index} differs");
    }

    [Fact]
    public void An_impure_composition_is_refused_rather_than_rendered_out_of_order()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("transitioned.html", Harness.Transitioned);
        var composition = harness.Cut.LoadComposition(name);
        var times = Enumerable.Range(0, 10).Select(i => i / 30.0).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ParallelRenderer(harness.Cut, NullLogger.Instance).RenderInOrder(
                composition,
                new SweepSpec { Composition = name, Width = 64, Height = 32, Times = times },
                times, 4, (_, _) => { }));

        Assert.Contains("not pure in t", ex.Message);
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
