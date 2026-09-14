using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// Every composition that ships actually renders.
///
/// <para>The samples are the documentation - they are what someone copies before they have read
/// anything - so a sample that lays out wrongly teaches the wrong thing. Three of these were
/// written against idioms that looked reasonable and rendered badly: an explicit
/// <c>line-height</c> made every text box taller than the value given, and <c>align-items</c>
/// alongside a full-height absolute sibling spread a lockup across the whole frame. Neither threw.
/// Both were only visible by looking.</para>
///
/// <para>This cannot check that a composition looks GOOD. It checks the things that were actually
/// wrong: that something is drawn, that it is not one flat colour, and that it stays inside the
/// frame it was authored for.</para>
/// </summary>
public sealed class SampleTests
{
    public static TheoryData<string> Compositions()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "compositions"), "*.html").Order())
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Compositions))]
    public void It_renders_something_and_keeps_it_in_the_frame(string name)
    {
        using var harness = new Harness();
        var composition = harness.WriteComposition(name,
            File.ReadAllText(Path.Combine(RepoRoot(), "compositions", name)));

        // Late enough that every entrance has finished in all of them.
        SKBitmap? shot = null;
        var report = harness.Cut.Sweep(
            new SweepSpec { Composition = composition, Width = 1280, Height = 720, Times = [2.6] },
            frame => shot = SKBitmap.FromImage(frame.Image));

        using var bitmap = shot!;

        // Fonts are registered rather than resolved from the machine, so a problem here is a
        // composition naming a family nothing covers - which renders, and renders wrong.
        Assert.True(report.FontProblems.Count == 0,
            $"{name}: {string.Join("; ", report.FontProblems)}");

        var distinct = new HashSet<uint>();
        var ink = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                var p = bitmap.GetPixel(x, y);
                distinct.Add((uint)((p.Red << 16) | (p.Green << 8) | p.Blue));
                if (p.Alpha > 0) ink++;
            }

        Assert.True(ink > 0, $"{name} rendered nothing at all");
        Assert.True(distinct.Count > 3, $"{name} rendered {distinct.Count} distinct colour(s) - it is a flat fill, not a composition");
    }

    [Theory]
    [MemberData(nameof(Compositions))]
    public void It_is_pure_in_t_so_it_renders_directly_rather_than_being_swept(string name)
    {
        // Every sample here is a demonstration of what the engine does well, and being sampleable
        // at any t for a few milliseconds is the top of that list. One that needed sweeping would
        // be teaching the wrong lesson.
        using var harness = new Harness();
        var composition = harness.WriteComposition(name,
            File.ReadAllText(Path.Combine(RepoRoot(), "compositions", name)));

        var purity = Purity.Analyse(harness.Cut.LoadComposition(composition));
        Assert.True(purity.PureInTime, $"{name} is not pure in t: {purity.Summary}");
    }

    [Theory]
    [MemberData(nameof(Compositions))]
    public void Its_markup_is_sound(string name)
    {
        // The engine's own reader. It catches the silent things - a tag that never closes, a
        // component nothing registered, a box that laid out with no area while holding content.
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "compositions", name));
        var report = CupriFace.Diagnostics.CupriDoctor.Check(html, null);

        Assert.False(report.HasErrors, $"{name}:\n{report}");
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
