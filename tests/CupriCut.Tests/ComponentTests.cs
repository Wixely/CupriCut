using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The engine's <c>cupri-*</c> elements, in a composition.
///
/// <para><b>Why this file exists.</b> They did not work at all, and nothing said so. A component
/// only expands when the document has been given a <see cref="CupriFace.Components.ComponentRegistry"/>,
/// and CupriCut's rendering path never gave it one - so every <c>cupri-*</c> element laid out at its
/// CSS size, painted NOTHING, settled cleanly and passed `lint`.</para>
///
/// <para>The studio wired its own registry from the beginning, so the window's controls worked and
/// the gap stayed invisible. Meanwhile `lint` reported <c>&lt;img&gt;</c> as CF0030 and told authors
/// to use <c>&lt;cupri-image&gt;</c> instead - which was right, and which then drew nothing. The one
/// substitution the tool actively recommends was the one that did not work.</para>
///
/// <para>Found while measuring claims for the authoring guide rather than by anything failing.</para>
/// </summary>
public class ComponentTests
{
    /// <summary>A 4x4 solid red PNG, generated rather than pasted - a corrupt paste is exactly how
    /// this investigation nearly concluded that images were broken upstream.</summary>
    private const string RedDot =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGN4oKAARwzEcQDgQxIBzrTn9gAAAABJRU5ErkJggg==";

    private const string Style =
        "<style>body,html{font-family:\"Noto Sans\";background:#000000;} .p{width:80px;height:80px;}</style>";

    [Fact]
    public void A_cupri_image_actually_paints()
    {
        using var harness = new Harness();
        var name = harness.WriteComposition("img.html",
            $"<cupri-image class=\"p\" src=\"data:image/png;base64,{RedDot}\"></cupri-image>{Style}");

        // 80x80 of it, and red. Not "something rendered" - the right number of the right pixels,
        // because the failure being guarded against paints a perfectly clean nothing.
        Assert.Equal(6400, RedPixels(harness, name));
    }

    [Fact]
    public void An_image_beside_the_composition_paints_too()
    {
        using var harness = new Harness();
        File.WriteAllBytes(Path.Combine(harness.Compositions, "dot.png"), Convert.FromBase64String(RedDot));
        var name = harness.WriteComposition("file.html",
            $"<cupri-image class=\"p\" src=\"dot.png\"></cupri-image>{Style}");

        Assert.Equal(6400, RedPixels(harness, name));
    }

    [Fact]
    public void A_raw_img_is_still_an_error_naming_the_alternative()
    {
        // The other half of the pair: the advice lint gives has to be advice that works, and the
        // test above is what makes it so.
        using var harness = new Harness();
        var name = harness.WriteComposition("raw.html",
            $"<img class=\"p\" src=\"data:image/png;base64,{RedDot}\">{Style}");

        var x = Inspector.Examine(harness.Cut, name);

        var found = Assert.Single(x.Findings, f => f.Code == "CF0030");
        Assert.Contains("cupri-image", found.Fix ?? found.What);
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
