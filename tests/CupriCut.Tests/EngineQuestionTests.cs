using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The two layout questions PLAN.md carried as <i>genuinely not understood</i>, measured on
/// CupriFace 0.41.0 and closed.
///
/// <para><b>Why they were open.</b> Both turned up while writing the sample compositions, against
/// 0.25.0, and both were written down rather than worked around silently - but each was found in a
/// real composition where other things were also wrong, and isolated probes of them behaved
/// correctly. The note left beside both said the same thing: <i>possibly also a line-height
/// artefact - it has not been re-tested since that was isolated</i>. That suspicion was right.
/// <see href="https://github.com/Wixely/CupriFace/issues/181">CupriFace#181</see> made a
/// <c>line-height</c> in <c>px</c> produce a line box three times too tall with the glyph at the
/// bottom of it, which pushed everything after it out of place, and it explains both reports: the
/// boxes were where they belonged and the text inside them was not.</para>
///
/// <para><b>Why the measurement stays.</b> Not because either is suspected now - both are exact -
/// but because a layout defect of this shape is silent. Nothing throws, nothing warns, and the
/// render is perfectly repeatable; the only thing that tells you is a number. Deleting the probe
/// once it passes is how a repository ends up re-opening the same question against the next
/// engine.</para>
/// </summary>
public sealed class EngineQuestionTests : IDisposable
{
    private readonly Harness _harness = new();
    private int _n;

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// <c>align-items: center</c> with a full-height <c>position: absolute</c> sibling sharing the
    /// flex line.
    ///
    /// <para>Reported as: centring worked in isolation, and spread a flex item's children across
    /// the full height once the absolute sibling was there. The open question was whether an
    /// absolutely positioned child still participates as a flex item.</para>
    ///
    /// <para><b>Answer on 0.41.0: it does not, and centring is unaffected.</b> The item lands at
    /// y=80 in a 200px line - exactly (200-40)/2 - and at the same place with the sibling as
    /// without it. Asserting the two against EACH OTHER as well as against the arithmetic is the
    /// point: "centred" and "centred whether or not something absolute shares the line" are two
    /// claims, and only the second one was ever in doubt.</para>
    /// </summary>
    [Fact]
    public void An_absolute_sibling_does_not_disturb_a_centred_flex_line()
    {
        const string Css = """
            .row{display:flex;align-items:center;width:400px;height:200px;position:relative;}
            .item{width:120px;}
            .b{width:100px;height:40px;background:#d9642a;}
            .tall{position:absolute;left:300px;top:0;width:10px;height:200px;background:#1133ff;}
            """;

        var with = Box("""
            <div class="row">
              <div class="item"><div class="b"></div></div>
              <div class="tall"></div>
            </div>
            """, Css);

        var without = Box("""
            <div class="row">
              <div class="item"><div class="b"></div></div>
            </div>
            """, Css);

        Assert.Equal(new Extent(0, 80, 100, 40), with);
        Assert.Equal(without, with);
    }

    /// <summary>
    /// A <c>position: absolute</c> child inside a nested positioned box.
    ///
    /// <para>Reported as: the children landed outside their container. The note recorded that
    /// isolated probes behaved correctly, so the cause was something else in the real composition.
    /// It was.</para>
    ///
    /// <para><b>Answer on 0.41.0: exact.</b> The offsets accumulate through both positioned
    /// ancestors and the margin on the outer one - 50+20+10 across and 30+10+10 down - to the
    /// pixel.</para>
    /// </summary>
    [Fact]
    public void An_absolute_child_of_a_nested_positioned_box_lands_inside_it()
    {
        var box = Box("""
            <div class="outer"><div class="inner"><div class="b"></div></div></div>
            """,
            """
            .outer{position:relative;width:400px;height:200px;margin-left:50px;margin-top:30px;}
            .inner{position:relative;left:20px;top:10px;width:200px;height:100px;}
            .b{position:absolute;left:10px;top:10px;width:50px;height:20px;background:#d9642a;}
            """);

        // 50 + 20 + 10 across, 30 + 10 + 10 down. Written as the sum rather than as 80 and 50, so
        // a failure says which hop was lost instead of only that the answer moved.
        Assert.Equal(new Extent(50 + 20 + 10, 30 + 10 + 10, 50, 20), box);
    }

    private readonly record struct Extent(int X, int Y, int W, int H)
    {
        public override string ToString() => $"x={X} y={Y} w={W} h={H}";
    }

    /// <summary>The bounding box of everything painted in the accent colour. Nothing else in these
    /// documents is that colour, and the one other coloured element is deliberately blue so it
    /// cannot be mistaken for the thing under test.</summary>
    private Extent Box(string body, string css)
    {
        var html = body + "<style>body,html{margin:0;font-family:\"Noto Sans\";background:#000000;}"
            + css + "</style>";
        var name = _harness.WriteComposition($"q{_n++}.html", html);
        var loaded = _harness.Cut.LoadComposition(name);

        using var doc = _harness.Cut.OpenDocument(loaded);

        // Settle BEFORE the frame, exactly as the sweep does - settling re-lays-out from zero.
        doc.Animate(0);
        doc.Settle(900, 300, TimeSpan.FromSeconds(5));
        doc.Animate(0);

        using var image = doc.RenderToImage(900, 300);
        using var bitmap = SKBitmap.FromImage(image);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red <= 180 || c.Green >= 140 || c.Blue >= 100) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        // -1 everywhere means nothing was painted at all, which is a distinct failure from being
        // painted in the wrong place and should read differently in the assertion message.
        return maxX < 0 ? new Extent(-1, -1, -1, -1) : new Extent(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
