using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// The marked backdrop, and the one mechanism that actually turns it off.
///
/// <para>Measured before it was written: the engine parses <c>--cupricut-background</c> as an
/// ordinary attribute, but a CSS attribute selector on it matches nothing and fails SILENTLY - the
/// backdrop renders as though the rule were absent. An inline <c>display:none</c> works, and beats
/// an author's own <c>display</c> rule. That is why the rewrite edits the tag.</para>
/// </summary>
public sealed class BackdropTests
{
    private const string Marked = """
        <div --cupricut-background="studio" class="bg"></div>
        <div class="fg"></div>
        <style>
          body, html { margin:0; font-family: "Noto Sans"; }
          .bg { position:absolute; left:0; top:0; width:100px; height:100px; background:#ff0000; }
          .fg { position:absolute; left:40px; top:40px; width:20px; height:20px; background:#00ff00; }
        </style>
        """;

    [Fact]
    public void A_marked_element_is_found_with_its_label_and_its_classes()
    {
        var found = Backdrop.Find(Marked);

        var one = Assert.Single(found);
        Assert.Equal("div", one.Tag);
        Assert.Equal("studio", one.Label);
        Assert.Equal("bg", one.Classes);
        Assert.Contains("studio", one.Describe());
    }

    [Fact]
    public void The_marker_needs_no_value()
    {
        var one = Assert.Single(Backdrop.Find("<section --cupricut-background class=\"sky\"></section>"));
        Assert.Null(one.Label);
        Assert.Equal("sky", one.Classes);
        Assert.Equal("<section class=\"sky\">", one.Describe());
    }

    [Theory]
    // A longer attribute that merely starts the same way is a different attribute.
    [InlineData("<div --cupricut-background-colour=\"red\"></div>")]
    // The name in prose is not a marker.
    [InlineData("<p>Set --cupricut-background on the element behind it.</p>")]
    [InlineData("<div class=\"bg\"></div>")]
    public void What_is_not_a_marker(string html)
    {
        Assert.Empty(Backdrop.Find(html));
        Assert.Equal(html, Backdrop.Hidden(html));
    }

    [Fact]
    public void Hiding_touches_the_opening_tag_and_nothing_else()
    {
        var hidden = Backdrop.Hidden(Marked);

        Assert.Contains("<div --cupricut-background=\"studio\" class=\"bg\" style=\"display:none\">", hidden);
        // Everything else is exactly as the author wrote it - the foreground included, so an
        // element that comes back when the flag flips is the one that was never hidden.
        Assert.Contains("<div class=\"fg\"></div>", hidden);
        Assert.Equal(Marked.Length + " style=\"display:none\"".Length, hidden.Length);
    }

    [Fact]
    public void An_existing_style_is_merged_rather_than_replaced()
    {
        var hidden = Backdrop.Hidden("<div --cupricut-background style=\"opacity:0.5\" class=\"bg\"></div>");

        // Appended, not prepended: the author's own declaration would otherwise win.
        Assert.Contains("style=\"opacity:0.5;display:none\"", hidden);
        // ...and the class written AFTER the style attribute survives in place.
        Assert.Contains("class=\"bg\"", hidden);
    }

    [Fact]
    public void Hiding_twice_hides_once()
    {
        var once = Backdrop.Hidden(Marked);
        Assert.Equal(once, Backdrop.Hidden(once));
    }

    [Fact]
    public void A_hidden_backdrop_is_still_reported_as_a_backdrop()
    {
        // Otherwise the answer would say "no backdrop" for exactly the render that hid one.
        Assert.Single(Backdrop.Find(Backdrop.Hidden(Marked)));
    }

    [Fact]
    public void The_flag_is_resolved_caller_then_project_then_shown()
    {
        var plain = Composition("x.html", Marked, null);
        Assert.False(Backdrop.Resolve(plain, null).BackdropHidden);          // nothing said: shown
        Assert.True(Backdrop.Resolve(plain, false).BackdropHidden);          // caller says hide
        Assert.False(Backdrop.Resolve(plain, true).BackdropHidden);

        var project = Composition("x.cut.json", Marked, new RenderSettings { ShowBackground = false });
        Assert.True(Backdrop.Resolve(project, null).BackdropHidden);         // project remembers
        Assert.False(Backdrop.Resolve(project, true).BackdropHidden);        // caller still wins
    }

    [Fact]
    public void Resolving_an_already_hidden_composition_changes_nothing()
    {
        var hidden = Backdrop.Resolve(Composition("x.html", Marked, null), false);
        Assert.Same(hidden, Backdrop.Resolve(hidden, false));
    }

    [Fact]
    public void The_backdrop_really_disappears_from_the_frame()
    {
        // The claim the whole feature rests on, tested by looking at pixels rather than at markup.
        using var harness = new Harness();
        var name = harness.WriteComposition("marked.html", Marked);

        Assert.Equal(new SKColor(0xFF, 0x00, 0x00), Corner(harness, name, showBackground: null));
        Assert.Equal(new SKColor(0xFF, 0x00, 0x00), Corner(harness, name, showBackground: true));
        // Hidden, the clear colour comes through where the backdrop was...
        Assert.Equal(SKColors.Blue, Corner(harness, name, showBackground: false));
        // ...and the foreground, which was never marked, is exactly where it was.
        Assert.Equal(new SKColor(0x00, 0xFF, 0x00), Middle(harness, name, showBackground: false));
    }

    [Fact]
    public void The_shipped_lower_third_marks_its_backdrop()
    {
        // The example is the documentation, so it has to stay true.
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "compositions", "lower-third.html"));
        var one = Assert.Single(Backdrop.Find(html));
        Assert.Equal("studio gradient", one.Label);
    }

    private static SKColor Corner(Harness harness, string name, bool? showBackground) =>
        Pixel(harness, name, showBackground, 5, 5);

    private static SKColor Middle(Harness harness, string name, bool? showBackground) =>
        Pixel(harness, name, showBackground, 50, 50);

    private static SKColor Pixel(Harness harness, string name, bool? showBackground, int x, int y)
    {
        SKColor found = default;
        harness.Cut.Sweep(
            new SweepSpec
            {
                Composition = name,
                Width = 100,
                Height = 100,
                Times = [0],
                Background = SKColors.Blue,
                ShowBackground = showBackground,
            },
            frame =>
            {
                using var bitmap = SKBitmap.FromImage(frame.Image);
                found = bitmap.GetPixel(x, y);
            });
        return found;
    }

    private static Composition Composition(string path, string html, RenderSettings? render) =>
        new(path, ".", html, null, [], [])
        {
            Project = render is null ? null : new CutProject { Render = render },
        };

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriCut.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
