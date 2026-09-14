using System.Diagnostics;
using CupriCut.Services;
using CupriCut.Gui;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// Can the preview keep up with the clock?
///
/// <para>Holding the document open is what makes real-time playback possible at all: opening and
/// settling costs 25-370 ms, and the frame itself costs single-digit milliseconds. These assert the
/// headroom rather than just print it, because losing it is exactly the regression that would turn
/// the scrub bar back into a slideshow.</para>
/// </summary>
public sealed class PlaybackSpeedTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("lower-third.html", 1280, 720)]
    [InlineData("revenue-card.html", 1280, 720)]
    [InlineData("timebase.html", 1280, 720)]
    public void A_held_document_renders_faster_than_real_time(string name, int w, int h)
    {
        using var harness = new Harness();
        var root = FindRepoRoot();
        var composition = harness.WriteComposition(name, File.ReadAllText(Path.Combine(root, "compositions", name)));

        var open = Stopwatch.StartNew();
        using var session = PreviewSession.Open(harness.Cut, composition, w, h, SKColors.White);
        open.Stop();

        // Warm, then measure 120 frames the way playback would ask for them.
        for (var i = 0; i < 10; i++) session.RenderAt(i / 120.0).Dispose();

        var sw = Stopwatch.StartNew();
        const int Frames = 120;
        for (var i = 0; i < Frames; i++) session.RenderAt(1.0 + i / 120.0).Dispose();
        sw.Stop();

        var msPerFrame = sw.Elapsed.TotalMilliseconds / Frames;
        output.WriteLine($"{name} at {w}x{h}");
        output.WriteLine($"  open+settle {open.ElapsedMilliseconds} ms (once)");
        output.WriteLine($"  {msPerFrame:0.00} ms/frame -> {1000 / msPerFrame:0} fps  [pure={session.Purity.PureInTime}]");
        output.WriteLine($"  real-time headroom at 30fps: {33.3 / msPerFrame:0.0}x   at 60fps: {16.7 / msPerFrame:0.0}x");

        // 30 fps with room to spare. Measured 3-7 ms a frame here, so this leaves a 4x margin for a
        // slower machine before it means anything is actually wrong.
        Assert.True(msPerFrame < 33.3,
            $"{msPerFrame:0.0} ms/frame cannot sustain 30 fps playback");
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
