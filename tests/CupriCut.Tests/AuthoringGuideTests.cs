using CupriFace;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The claims <c>docs/AUTHORING.md</c> makes about the engine, asserted.
///
/// <para><b>Why.</b> That document exists because this engine differs from a browser in ways that
/// are silent - an animation that is accepted, runs, and changes nothing. A guide to silent
/// behaviour is worthless the moment it goes stale, and it goes stale without anyone finding out:
/// three of its predecessor's claims were true of 0.25.0 and false by 0.25.1.</para>
///
/// <para>So the guide is not prose about the engine - it is a description of what these tests
/// measure. When the engine changes, this file fails, and the document is KNOWN to be wrong rather
/// than quietly becoming so.</para>
/// </summary>
public class AuthoringGuideTests : IDisposable
{
    // Through the Harness, not CupriDocument.Load, for two reasons.
    //
    // The first is that it CRASHED the test host on Linux CI - not a failed assertion, a native
    // abort - because a bare document has no registered face, and the runner has no system font to
    // fall back on. 319 tests passed and then the process died.
    //
    // The second is the one that would matter even if it had not: this file quotes exact pixel
    // numbers, and the guide quotes them back. A measurement taken against whatever font the
    // machine happened to have is not a measurement of anything. The Harness registers the Noto
    // Sans this repository ships and sets FontPolicy.RegisteredOnly, so 24px is 24px everywhere.
    private readonly Harness _harness = new();
    private int _n;

    public void Dispose() => _harness.Dispose();

    // ---- "What actually animates" ------------------------------------------------------------

    [Theory]
    [InlineData("width", ".b{height:40px;} @keyframes k{from{width:10px;}to{width:400px;}}")]
    [InlineData("height", ".b{width:200px;} @keyframes k{from{height:10px;}to{height:120px;}}")]
    [InlineData("opacity", ".b{width:200px;height:40px;} @keyframes k{from{opacity:1;}to{opacity:0;}}")]
    [InlineData("translateX", ".b{width:100px;height:40px;} @keyframes k{from{transform:translateX(0);}to{transform:translateX(300px);}}")]
    [InlineData("scale", ".b{width:100px;height:40px;} @keyframes k{from{transform:scale(1);}to{transform:scale(2);}}")]
    [InlineData("rotate", ".b{width:200px;height:40px;} @keyframes k{from{transform:rotate(0deg);}to{transform:rotate(45deg);}}")]
    public void These_properties_animate(string property, string css)
    {
        Assert.True(Box(Doc(css), 0) != Box(Doc(css), 2), $"{property} did not move the box.");
    }

    [Theory]
    [InlineData("margin-left", ".b{width:100px;height:40px;} @keyframes k{from{margin-left:0;}to{margin-left:300px;}}")]
    [InlineData("left", ".b{position:absolute;top:0;width:100px;height:40px;} @keyframes k{from{left:0;}to{left:300px;}}")]
    [InlineData("top", ".b{position:absolute;left:0;width:100px;height:40px;} @keyframes k{from{top:0;}to{top:150px;}}")]
    [InlineData("padding", ".b{width:100px;height:40px;} @keyframes k{from{padding:0;}to{padding:40px;}}")]
    [InlineData("border-radius", ".b{width:100px;height:100px;} @keyframes k{from{border-radius:0;}to{border-radius:50px;}}")]
    [InlineData("visibility", ".b{width:200px;height:40px;} @keyframes k{from{visibility:visible;}to{visibility:hidden;}}")]
    [InlineData("display", ".b{width:200px;height:40px;} @keyframes k{from{display:block;}to{display:none;}}")]
    public void These_do_nothing_and_say_nothing(string property, string css)
    {
        // The dangerous half. Each is accepted, runs, and changes not one pixel - so the author
        // sees "my animation did not play" rather than an error. The guide lists them for that
        // reason; this keeps the list true.
        Assert.True(Box(Doc(css), 0) == Box(Doc(css), 2), $"{property} now animates - the guide needs revisiting.");
    }

    [Fact]
    public void Colour_cannot_be_animated_at_all()
    {
        // Which is why the guide says to cross-fade two stacked elements instead. There is no
        // workaround at the property level: every form of it is inert.
        Assert.Equal(Centre(Doc(".b{width:200px;height:40px;} @keyframes k{from{background-color:#d9642a;}to{background-color:#3f6fd8;}}"), 2), Accent);
        Assert.Equal(Centre(Doc(".b{width:200px;height:40px;} @keyframes k{from{background:#d9642a;}to{background:#3f6fd8;}}"), 2), Accent);
        Assert.Equal(
            Centre(Doc(".b{width:200px;height:40px;} @keyframes k{from{background:linear-gradient(90deg,#d9642a,#d9642a);}to{background:linear-gradient(90deg,#3f6fd8,#3f6fd8);}}"), 2),
            Accent);
    }

    // ---- "One animation per element. Exactly one." -------------------------------------------

    [Fact]
    public void A_comma_separated_list_runs_NEITHER_animation()
    {
        // Not "the first one wins": the shorthand fails to parse and is dropped entirely, leaving
        // the element at its static CSS values. Silently.
        const string Css = """
            .b{width:10px;height:40px;animation:grow 2s linear both, drop 2s linear both;}
            @keyframes grow{from{width:10px;}to{width:300px;}}
            @keyframes drop{from{height:40px;}to{height:150px;}}
            """;

        var box = Box(Doc(Css, animate: false), 2);

        Assert.Equal(10, box.W);
        Assert.Equal(40, box.H);
    }

    // ---- "Delays, and the one that silently does not work" -----------------------------------

    [Theory]
    [InlineData("1s")]
    [InlineData("var(--d)")]
    public void A_delay_is_honoured(string delay)
    {
        Assert.Equal(10, Delayed(delay, at: 0.5).W);     // still at its start value
        Assert.Equal(300, Delayed(delay, at: 2.5).W);    // and finished later
    }

    [Fact]
    public void A_delay_written_as_calc_is_silently_treated_as_zero()
    {
        // The element plays immediately and is finished long before you expected it to start, with
        // no diagnostic. This is the whole reason stagger goes in the keyframe percentages.
        Assert.True(Delayed("calc(var(--d) + 0.5s)", at: 0.5).W > 100,
            "calc() in animation-delay now works - docs/AUTHORING.md needs revisiting.");
    }

    // ---- "Timing functions work" -------------------------------------------------------------

    [Fact]
    public void Easing_is_applied_rather_than_ignored()
    {
        // ease-out is cubic-bezier(0, 0, .58, 1): about 68.5% of the way at the halfway point, so a
        // 10->300px width reads about 208 there. Linear would be 155. Worth asserting because
        // assuming linear is how bar-race.html came to declare its overtake six frames late.
        const string Css = """
            .b{width:10px;height:40px;animation:grow 2s ease-out both;}
            @keyframes grow{from{width:10px;}to{width:300px;}}
            """;

        Assert.InRange(Box(Doc(Css, animate: false), 1).W, 195, 220);
    }

    // ---- "Layout and paint" ------------------------------------------------------------------

    [Fact]
    public void Line_height_is_correct_in_every_unit()
    {
        // Through 0.25.0 a px value produced a box 3x too tall and em/% were ignored outright,
        // which is why no shipped composition sets one. CupriFace#181.
        Assert.Equal(24, LineBox(""));                    // 20px x 1.2
        Assert.Equal(20, LineBox("line-height:20px;"));
        Assert.Equal(30, LineBox("line-height:1.5em;"));
        Assert.Equal(30, LineBox("line-height:150%;"));
    }

    [Fact]
    public void Letter_spacing_is_still_ignored()
    {
        Assert.Equal(TextWidth(""), TextWidth("letter-spacing:8px;"));
    }

    [Fact]
    public void A_repeating_gradient_paints_nothing()
    {
        // It parses, so the only sign is an empty box. Use one gradient with hard stops.
        Assert.Equal(-1, Box(Raw(".b{width:200px;height:60px;background:repeating-linear-gradient(90deg,#d9642a 0 20px,#111 20px 40px);}"), 0).W);
        Assert.Equal(200, Box(Raw(".b{width:200px;height:60px;background:linear-gradient(90deg,#d9642a 0%,#d9642a 100%);}"), 0).W);
    }

    [Fact]
    public void A_property_is_reported_from_the_declaration_and_not_from_the_prose()
    {
        // CF0051 arrived in 0.25.1 as a substring search over the whole document, so a file could
        // not say IN A COMMENT that it avoids repeating gradients without failing its own lint -
        // and a composition that merely PAINTED the words failed too. CupriFace#188, fixed in
        // 0.26.1. caption-strip.html carried the workaround of not writing the name down.
        //
        // Asserted for letter-spacing as well, since CF0050 was the check that always got this
        // right and is what the fix made CF0051 match.
        Assert.Equal(1, Reported("CF0051",
            Head + ".b{width:200px;height:60px;background:repeating-linear-gradient(90deg,#d9642a 0 20px,#111 20px 40px);}</style>"));

        Assert.Equal(0, Reported("CF0051",
            Head + "/* deliberately not a repeating-linear-gradient() */.b{width:200px;height:60px;background:#d9642a;}</style>"));

        Assert.Equal(0, Reported("CF0051",
            "<!-- avoids a repeating-linear-gradient() on purpose -->" + Head + ".b{width:200px;height:60px;background:#d9642a;}</style>"));

        Assert.Equal(0, Reported("CF0050",
            Head + "/* letter-spacing is ignored here */.b{width:200px;height:60px;background:#d9642a;}</style>"));
    }

    [Fact]
    public void A_single_border_side_paints()
    {
        // Fixed in 0.25.0. The studio's three separators had never been drawn.
        Assert.Equal(20, Box(Raw(".b{width:0;height:60px;border-left:20px solid #d9642a;}"), 0).W);
        Assert.Equal(20, Box(Raw(".b{width:60px;height:0;border-bottom:20px solid #d9642a;}"), 0).H);
    }

    [Fact]
    public void Overflow_hidden_clips_to_the_border_radius()
    {
        const string Html = """
            <div class="ring"><div class="b"></div></div>
            <style>
              body,html{font-family:"Noto Sans";background:#000;}
              .ring{width:200px;height:200px;border-radius:100px;overflow:hidden;}
              .b{width:200px;height:200px;background:#d9642a;}
            </style>
            """;

        using var bitmap = Render(Html, 0);

        Assert.Equal(Accent, Opaque(bitmap.GetPixel(100, 100)));   // the centre is painted
        Assert.NotEqual(Accent, Opaque(bitmap.GetPixel(3, 3)));    // the square corner is cut away
    }

    [Fact]
    public void Align_items_centres_but_align_self_and_auto_margins_do_not()
    {
        Assert.Equal(80, Centred("align-items:center;", ""));
        Assert.Equal(0, Centred("", "align-self:center;"));
        Assert.Equal(0, Centred("", "margin:auto 0;"));
    }

    [Fact]
    public void A_var_fallback_is_honoured_for_lengths_and_for_colours()
    {
        Assert.Equal(150, Box(Raw(".b{width:var(--w,150px);height:40px;background:#d9642a;}"), 0).W);
        Assert.Equal(100, Box(Raw(".b{width:100px;height:40px;background:var(--c,#d9642a);}"), 0).W);
    }

    // ---- machinery ---------------------------------------------------------------------------

    /// <summary>How many findings of one code a document produces.</summary>
    private static int Reported(string code, string html) =>
        CupriCut.Services.Doctor.Check(html, string.Empty, 1280, 720).Count(f => f.Code == code);

    private static readonly SKColor Accent = new(0xd9, 0x64, 0x2a);

    private static SKColor Opaque(SKColor c) => new(c.Red, c.Green, c.Blue);

    private const string Head = "<div class=\"b\"></div><style>"
        + "body,html{font-family:\"Noto Sans\";background:#000;}";

    /// <summary>A document whose only painted thing is one accent-coloured box.</summary>
    private static string Doc(string css, bool animate = true) =>
        Head + ".b{background:#d9642a;" + (animate ? "animation:k 2s linear both;" : "") + "}"
        + css + "</style>";

    private static string Raw(string css) => Head + css + "</style>";

    private Extent Delayed(string delay, double at) => Box(
        Head + ":root{--d:1s;}"
        + ".b{width:10px;height:40px;background:#d9642a;animation:grow 1s linear " + delay + " both;}"
        + "@keyframes grow{from{width:10px;}to{width:300px;}}</style>", at);

    private int LineBox(string rule) =>
        Box(Raw(".b{font-size:20px;width:400px;background:#d9642a;" + rule + "}"), 0, "One line").H;

    private int TextWidth(string rule) =>
        Box(Raw(".b{font-size:40px;width:800px;color:#d9642a;" + rule + "}"), 0, "MMMMM").W;

    private int Centred(string parent, string child) => Box(
        "<div class=\"row\"><div class=\"b\"></div></div><style>"
        + "body,html{font-family:\"Noto Sans\";background:#000;}"
        + ".row{display:flex;width:400px;height:200px;" + parent + "}"
        + ".b{width:100px;height:40px;background:#d9642a;" + child + "}</style>", 0).Y;

    private readonly record struct Extent(int X, int Y, int W, int H);

    private SKBitmap Render(string html, double t, string text = "")
    {
        if (text.Length > 0)
            html = html.Replace("<div class=\"b\"></div>", $"<div class=\"b\">{text}</div>");

        var name = _harness.WriteComposition($"probe{_n++}.html", html);
        var loaded = _harness.Cut.LoadComposition(name);

        using var doc = _harness.Cut.OpenDocument(loaded);

        // Settle BEFORE the frame you want, exactly as CupriCutService.Sweep does. Settling
        // re-lays-out from zero, so animating first and settling after throws the frame away and
        // renders t=0 - which reads as "the animation did not run" and is how this file spent a
        // build reporting that width does not animate.
        doc.Animate(0);
        doc.Settle(900, 300, TimeSpan.FromSeconds(5));
        doc.Animate(t);

        using var image = doc.RenderToImage(900, 300);
        return SKBitmap.FromImage(image);
    }

    /// <summary>The bounding box of everything painted in the accent colour, which is the element
    /// under test - nothing else in these documents is that colour. Width -1 means nothing at all
    /// was painted, which several of these tests are specifically about.</summary>
    private Extent Box(string html, double t, string text = "")
    {
        using var bitmap = Render(html, t, text);

        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.Red != Accent.Red || c.Green != Accent.Green || c.Blue != Accent.Blue) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        return maxX < 0 ? new Extent(-1, -1, -1, -1) : new Extent(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>The colour at a point the element certainly covers.</summary>
    private SKColor Centre(string html, double t)
    {
        using var bitmap = Render(html, t);
        return Opaque(bitmap.GetPixel(20, 20));
    }
}
