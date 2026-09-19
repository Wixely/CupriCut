using CupriCut.Services;
using CupriCut.Tools;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriCut.Tests;

/// <summary>
/// A project carrying its own typeface.
///
/// <para><b>Why this had to be settled.</b> A <c>.cutpkg</c> is "one file you can hand to another
/// machine", and that promise was only as good as the fonts: assets inline as <c>data:</c> URIs,
/// but <see cref="CupriFace.Text.FontPolicy.RegisteredOnly"/> reads faces from
/// <c>Cut:FontDirectories</c> rather than from the document, so it was not obvious that an
/// embedded face could ever satisfy the strict policy. If it could not, every project using a
/// non-stock typeface was quietly machine-dependent.</para>
///
/// <para><b>It can, and it already did.</b> The engine registers a family from an
/// <c>@font-face</c> rule whose <c>src</c> is a <c>data:</c> URI, and CupriCut already rewrites
/// <c>url('name')</c> in a project's CSS to whatever that asset holds. So <c>attach_asset</c> plus
/// three lines of CSS is the whole feature. No code was written for this - these tests are what
/// stop it being lost.</para>
///
/// <para>The harness here is given NO font directory, so the embedded face is the only thing that
/// could possibly answer. A test that left the shipped Noto Sans in place would pass whether or
/// not <c>@font-face</c> worked at all.</para>
/// </summary>
public class EmbeddedFontTests(ITestOutputHelper output)
{
    private const string Family = "House Sans";

    [Fact]
    public void A_project_can_carry_the_face_it_asks_for()
    {
        using var harness = Bare();
        Typeset(harness, "typed" + CutPackage.Extension);

        var loaded = harness.Cut.LoadComposition("typed" + CutPackage.Extension);
        using var doc = harness.Cut.OpenDocument(loaded);
        doc.Animate(0);
        doc.Settle(480, 160, TimeSpan.FromSeconds(5));
        using var image = doc.RenderToImage(480, 160);

        var report = doc.FontReport;
        output.WriteLine($"registered: {string.Join(", ", report.RegisteredFamilies)}");
        foreach (var r in report.Resolutions)
            output.WriteLine($"  '{r.Family}' -> {r.ResolvedFamily} ({r.Source})");

        // Registered, not Platform: the difference between a render CI reproduces and one it
        // merely resembles, which is the whole reason the policy is strict.
        var resolution = Assert.Single(report.Resolutions, r => r.Family == Family);
        Assert.Equal(CupriFace.Text.FontSource.Registered, resolution.Source);
        Assert.True(report.IsDeterministic);

        using var bitmap = SKBitmap.FromImage(image);
        Assert.True(Ink(bitmap) > 500, "the text did not paint");
    }

    [Fact]
    public void Without_the_face_the_same_project_is_refused_rather_than_substituted()
    {
        // The other half, and the one that proves the first is not passing by accident. Same CSS,
        // same family, no asset - and under RegisteredOnly that is an error naming the family
        // rather than a quiet substitution.
        using var harness = Bare();
        harness.Cut.SaveProject("bare" + CutPackage.Extension, Project());

        var loaded = harness.Cut.LoadComposition("bare" + CutPackage.Extension);
        using var doc = harness.Cut.OpenDocument(loaded);

        var ex = Assert.ThrowsAny<Exception>(() =>
        {
            doc.Animate(0);
            doc.Settle(480, 160, TimeSpan.FromSeconds(5));
            using var _ = doc.RenderToImage(480, 160);
        });

        output.WriteLine(ex.Message);
        Assert.Contains(Family, ex.Message);
    }

    [Fact]
    public void The_embedded_face_survives_the_round_trip_through_a_package()
    {
        using var harness = Bare();
        Typeset(harness, "typed" + CutPackage.Extension);

        var reopened = harness.Cut.LoadProject("typed" + CutPackage.Extension);

        Assert.True(reopened.Assets.ContainsKey("housefont.ttf"));
        Assert.StartsWith("data:font/ttf;base64,", reopened.Assets["housefont.ttf"]);
        Assert.Contains("url('housefont.ttf')", reopened.Css);
    }

    [Fact]
    public void A_package_stores_the_face_as_bytes_rather_than_base64()
    {
        // 631 KB of TTF is 842 KB once base64'd. The container is what makes carrying a typeface
        // a reasonable thing to do rather than a thing that doubles the project.
        using var harness = Bare();
        Typeset(harness, "json");
        Typeset(harness, "packed" + CutPackage.Extension);

        var projects = Path.Combine(harness.Root, "projects");
        long json = new FileInfo(Path.Combine(projects, "json.cut.json")).Length;
        long packed = new FileInfo(Path.Combine(projects, "packed" + CutPackage.Extension)).Length;

        output.WriteLine($"json {json:N0} bytes, package {packed:N0} bytes");
        Assert.True(packed < json * 0.85, $"expected the package to be well under the JSON; {packed:N0} vs {json:N0}");
    }

    [Fact]
    public void A_project_carrying_its_own_face_lints_clean()
    {
        using var harness = Bare();
        Typeset(harness, "typed" + CutPackage.Extension);

        var x = Inspector.Examine(harness.Cut, "typed" + CutPackage.Extension);

        output.WriteLine(string.Join("\n", x.Findings.Select(f => $"{f.Level} {f.Code}: {f.What}")));
        Assert.Equal("clean", x.Verdict);
        Assert.DoesNotContain(x.Findings, f => f.Code == Inspector.MachineFont);
    }

    // ---- fixtures ----------------------------------------------------------------------------

    /// <summary>A service with NO font directory, so nothing but the project can answer.</summary>
    private static Harness Bare() => new(o => o.FontDirectories = ["no-such-directory"]);

    private static CutProject Project() => new()
    {
        Html = "<div class=\"t\">Handgloves</div>",
        Css = $"@font-face{{font-family:\"{Family}\";src:url('housefont.ttf');}}"
              + $".t{{font-family:\"{Family}\";font-size:48px;color:#d9642a;}}",
        Render = new RenderSettings { Width = 480, Height = 160, Fps = 30, Duration = 1 },
    };

    /// <summary>A project that asks for <see cref="Family"/> and carries a face for it.</summary>
    private static void Typeset(Harness harness, string name)
    {
        var source = Directory
            .EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fonts"), "*.ttf").Order().First();
        File.Copy(source, Path.Combine(harness.Compositions, "housefont.ttf"), overwrite: true);

        harness.Cut.SaveProject(name, Project());
        ProjectTools.AttachAsset(harness.Cut, name, "housefont.ttf");
    }

    private static int Ink(SKBitmap bitmap)
    {
        var n = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Alpha > 8 && c.Red > 100 && c.Green < 160) n++;
        }
        return n;
    }
}
