using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// Inline <c>&lt;svg&gt;</c> in a composition.
///
/// <para><b>The same shape of gap as the missing component registry, and found the same way.</b>
/// The engine's parser keeps an <c>&lt;svg&gt;</c> whether or not anything can draw it, so without
/// the optional <c>CupriFace.Svg</c> package installed it laid out at its CSS size, painted
/// NOTHING, settled cleanly and passed <c>lint</c> - present, sized and empty, which every
/// automatic check calls fine. Counting pixels is the only thing that tells the difference, which
/// is why this file counts them rather than asserting that a render happened.</para>
///
/// <para>It matters more than a missing nicety because of what SVG usually is: an icon set or a
/// logo. A renderer that drops a brand asset in silence is worse than one that refuses it.</para>
///
/// <para>The lint half is the pair to it, and the reason the two live in one file. The engine's
/// <c>&lt;svg&gt;</c> check reads a marker <c>UseSvg</c> writes during the rebuild, so a checker
/// that was not told what the real renderer installs reports every inline SVG as undrawable and
/// advises installing a package this tool already ships. CupriCut has shipped advice that was
/// wrong twice before - a null stylesheet that turned the CSS checks off entirely, and CUT006 -
/// and a lint that is wrong costs more than a missing one.</para>
/// </summary>
public class SvgTests
{
    private const string Style =
        "<style>body,html{font-family:\"Noto Sans\";background:#000000;}</style>";

    /// <summary>80x80 of solid red, declared as a shape rather than as an image. The drawing's own
    /// width and height reach layout as an intrinsic size, the way an image's pixels do.</summary>
    private const string RedSquare =
        """<svg width="80" height="80" viewBox="0 0 80 80"><rect x="0" y="0" width="80" height="80" fill="#ff0000"/></svg>""";

    [Fact]
    public void An_inline_svg_actually_paints()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("svg.html", RedSquare + Style);

        // The right number of the right pixels. "Something rendered" would have passed throughout
        // the entire period this did not work.
        Assert.Equal(6400, RedPixels(harness, name));
    }

    [Fact]
    public void A_drawable_inline_svg_is_not_reported_as_a_gap()
    {
        // The other half: the renderer drawing it and the checker knowing that it draws it are two
        // different facts, and only one of them is about pixels.
        using var harness = new Harness();
        var name = harness.WriteComposition("drawable.html", RedSquare + Style);

        var x = Inspector.Examine(harness.Cut, name);

        Assert.DoesNotContain(x.Findings, f => f.Code == "CF0030");
    }

    [Fact]
    public void An_svg_with_nothing_in_it_is_still_reported()
    {
        // Deliberately still a warning, and nothing above should have silenced it. An empty
        // <svg> stays empty with the package installed - there is no drawing to claim - so this
        // is the case where the markup really is a box that paints nothing, and saying so is the
        // whole job. A composition whose shapes were going to be supplied by a script has no
        // script here.
        using var harness = new Harness();
        var name = harness.WriteComposition("empty.html",
            "<svg width=\"80\" height=\"80\"></svg>" + Style);

        var x = Inspector.Examine(harness.Cut, name);

        Assert.Contains(x.Findings, f => f.Code == "CF0030");
    }

    private static int RedPixels(Harness harness, string composition)
    {
        var loaded = harness.Cut.LoadComposition(composition);
        using var doc = harness.Cut.OpenDocument(loaded);
        doc.Animate(0);
        doc.Settle(400, 200, TimeSpan.FromSeconds(5));

        using var image = doc.RenderToImage(400, 200);
        using var bitmap = SKBitmap.FromImage(image);

        var red = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red > 180 && c.Green < 80 && c.Blue < 80) red++;
        }

        return red;
    }
}
