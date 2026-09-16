using CupriCut.Services;
using SkiaSharp;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// <c>data-start</c> / <c>data-duration</c> / <c>data-track</c>: what is on screen when.
///
/// <para>Everything before this rendered ONE scene - a single continuous motion. A video is scenes
/// in sequence, and this is what lets a composition say so.</para>
///
/// <para>The mechanism is measured rather than assumed, because three plausible ones do not work on
/// this engine: <c>visibility</c> and <c>display</c> are not animatable at all, and an element
/// cannot carry two animations (comma-separated, shorthand or longhand - neither applies). What
/// does work is one generated <c>@keyframes</c> per timed element with ADJACENT opacity stops, so
/// the change is a cut and not a fade.</para>
/// </summary>
public sealed class TimelineTests
{
    /// <summary>Three scenes, one after another, in a 6-second timeline. Each is a full-frame
    /// colour so a single pixel says which is showing.</summary>
    private const string Scenes = """
        <div class="scene red"    data-start="0" data-duration="2" data-track="main"></div>
        <div class="scene green"  data-start="2" data-duration="2" data-track="main"></div>
        <div class="scene blue"   data-start="4" data-duration="2" data-track="main"></div>
        <style>
          body, html { margin:0; font-family:"Noto Sans"; }
          .scene { position:absolute; left:0; top:0; width:100%; height:100%; }
          .red   { background-color:#ff0000; }
          .green { background-color:#00ff00; }
          .blue  { background-color:#0000ff; }
        </style>
        """;

    // ---- reading the timeline -------------------------------------------------------------------

    [Fact]
    public void The_windows_are_read_out_of_the_markup()
    {
        var plan = Timeline.Plan(Scenes, null);

        Assert.Equal(3, plan.Windows.Count);
        Assert.Equal([0, 2, 4], plan.Windows.Select(w => w.Start));
        Assert.Equal([2, 4, 6], plan.Windows.Select(w => w.End));
        Assert.Equal(6, plan.Duration);
        Assert.Equal(["main"], plan.Tracks);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void A_duration_that_is_not_given_means_until_the_end()
    {
        var plan = Timeline.Plan("""<div data-start="1.5"></div>""", null, projectDuration: 5);

        var one = Assert.Single(plan.Windows);
        Assert.Equal(1.5, one.Start);
        Assert.True(double.IsPositiveInfinity(one.End));
        Assert.Equal(5, plan.Duration);          // from the project, since the window has no end
        Assert.Contains("from 1.5s", one.Describe());
    }

    [Fact]
    public void The_timeline_is_never_shorter_than_the_project_says()
    {
        // A project that runs 10s with a scene ending at 2s still runs 10s.
        Assert.Equal(10, Timeline.Plan(Scenes, null, projectDuration: 10).Duration);
        // ...and a scene running past the project's duration extends it rather than being cut off.
        Assert.Equal(6, Timeline.Plan(Scenes, null, projectDuration: 3).Duration);
    }

    [Fact]
    public void A_composition_with_no_timeline_costs_nothing_and_is_left_alone()
    {
        var plain = Composition("plain.html", Harness.Keyframed, null);

        Assert.False(Timeline.Marks(Harness.Keyframed));
        Assert.Empty(Timeline.Plan(Harness.Keyframed, null).Windows);
        Assert.Same(plain, Timeline.Apply(plain));
    }

    [Theory]
    [InlineData("""<div data-start="soon"></div>""", "data-start")]
    [InlineData("""<div data-start="0" data-duration="ages"></div>""", "data-duration")]
    public void A_time_that_is_not_a_number_is_reported_rather_than_guessed(string html, string named)
    {
        var plan = Timeline.Plan(html, null);
        Assert.Contains(plan.Problems, p => p.Contains(named));
    }

    [Fact]
    public void A_timed_element_with_its_own_animation_is_reported()
    {
        // The engine runs ONE animation per element, so the window and the author's cannot both
        // exist. Losing theirs silently would be the worst outcome.
        const string Css = ".scene { animation: fade 1s linear both; }";
        var plan = Timeline.Plan("""<div class="scene" data-start="1"></div>""", Css);

        var problem = Assert.Single(plan.Problems);
        Assert.Contains("one animation per element", problem);
        Assert.Contains("Move the motion onto a child", problem);
    }

    [Theory]
    // A DESCENDANT of the scene animating is the shape this feature recommends, and the selector
    // contains ".scene" - warning about it would fire on every correctly-written composition.
    [InlineData(".scene .title { animation: rise 1s linear both; }")]
    [InlineData(".scene > .title { animation: rise 1s linear both; }")]
    [InlineData(".title { animation: rise 1s linear both; }")]
    // ...and a longer class name that merely starts the same way is a different class.
    [InlineData(".scenery { animation: rise 1s linear both; }")]
    public void An_animation_that_is_not_on_the_timed_element_is_not_reported(string css) =>
        Assert.Empty(Timeline.Plan("""<div class="scene" data-start="1"></div>""", css).Problems);

    [Theory]
    [InlineData(".scene { animation: fade 1s linear both; }")]
    [InlineData(".scene { animation-name: fade; }")]
    [InlineData(".wrap .scene { animation: fade 1s linear both; }")]
    [InlineData(".other, .scene { animation: fade 1s linear both; }")]
    [InlineData(".scene:hover { animation: fade 1s linear both; }")]
    public void An_animation_ON_the_timed_element_is_reported(string css) =>
        Assert.Single(Timeline.Plan("""<div class="scene" data-start="1"></div>""", css).Problems);

    [Fact]
    public void An_inline_style_block_is_searched_as_well_as_a_stylesheet()
    {
        // A plain .html composition keeps its rules in <style>, so the css argument is null for
        // exactly the compositions most people write.
        const string Html = """
            <div class="scene" data-start="1"></div>
            <style>.scene { animation: fade 1s linear both; }</style>
            """;
        Assert.Single(Timeline.Plan(Html, null).Problems);
    }

    [Fact]
    public void A_time_that_will_not_parse_does_not_slide_the_rewrite()
    {
        // Plan skips an element whose data-start is not a number; Rewrite used to consume a window
        // for it anyway, and the indices slid until one ran off the end of the list. The two share
        // one decision now.
        const string Html = """
            <div class="a" data-start="soon"></div>
            <div class="b" data-start="1"></div>
            """;
        var applied = Timeline.Apply(Composition("s.html", Html, null));

        Assert.Contains("class=\"b cut-t0\"", applied.Html);
        Assert.DoesNotContain("cut-t1", applied.Html);
    }

    // ---- the rewrite ----------------------------------------------------------------------------

    [Fact]
    public void The_rewrite_only_adds_a_class_and_appends_css()
    {
        var applied = Timeline.Apply(Composition("s.html", Scenes, null));

        Assert.Contains("class=\"scene red cut-t0\"", applied.Html);
        Assert.Contains("class=\"scene green cut-t1\"", applied.Html);
        // Everything the author wrote is still there, attributes included.
        Assert.Contains("data-start=\"2\" data-duration=\"2\" data-track=\"main\"", applied.Html);
        Assert.Contains("@keyframes cut-w0", applied.Css);
        Assert.Contains("--cut-start: 2s", applied.Css);
        Assert.True(applied.TimelineApplied);
    }

    [Fact]
    public void Applying_it_twice_applies_it_once()
    {
        var once = Timeline.Apply(Composition("s.html", Scenes, null));
        var twice = Timeline.Apply(once);

        Assert.Same(once, twice);
        Assert.Equal(1, Count(once.Html, "cut-t0"));
        Assert.Equal(1, Count(once.Css!, "@keyframes cut-w0"));
    }

    [Fact]
    public void An_element_with_no_class_gets_one()
    {
        var applied = Timeline.Apply(Composition("s.html", """<section data-start="1"></section>""", null));
        Assert.Contains("class=\"cut-t0\"", applied.Html);
    }

    [Fact]
    public void The_cut_is_a_cut_rather_than_a_fade()
    {
        // Adjacent stops either side of the boundary. A single stop would interpolate, which is
        // correct CSS for opacity and wrong for a scene change.
        var css = Timeline.Apply(Composition("s.html", Scenes, null)).Css!;

        // Scene two of a 6s timeline opens at 2s and closes at 4s. Asserted structurally rather
        // than as literal percentages: the point is that the two stops either side of a boundary
        // are DISTINCT and a hair apart, not what rounding produced.
        var stops = Stops(css, "cut-w1");

        Assert.Equal(6, stops.Count);
        var (openHidden, openShown) = (stops[1], stops[2]);
        Assert.Equal(0, openHidden.Opacity);
        Assert.Equal(1, openShown.Opacity);
        Assert.True(openShown.Percent > openHidden.Percent, "the cut must step forwards");
        Assert.True(openShown.Percent - openHidden.Percent < 0.001,
            $"the stops are {openShown.Percent - openHidden.Percent}% apart - that is a fade, not a cut");

        // 2s of 6s.
        Assert.Equal(100.0 / 3, openShown.Percent, 3);
        // 4s of 6s.
        Assert.Equal(200.0 / 3, stops[3].Percent, 3);
    }

    // ---- and it actually renders that way -------------------------------------------------------

    [Theory]
    [InlineData(0.0, 0xFF, 0x00, 0x00)]
    [InlineData(1.9, 0xFF, 0x00, 0x00)]
    [InlineData(2.1, 0x00, 0xFF, 0x00)]
    [InlineData(3.9, 0x00, 0xFF, 0x00)]
    [InlineData(4.1, 0x00, 0x00, 0xFF)]
    [InlineData(5.9, 0x00, 0x00, 0xFF)]
    public void Only_the_scene_whose_window_contains_t_is_on_screen(double t, byte r, byte g, byte b)
    {
        // The claim the whole feature rests on, in pixels, at the sample times the plan asked for.
        using var harness = new Harness();
        var name = harness.WriteComposition("scenes.html", Scenes);

        Assert.Equal(new SKColor(r, g, b), PixelAt(harness, name, t));
    }

    [Fact]
    public void A_scene_carries_its_start_into_its_subtree()
    {
        // The engine's clock is absolute, so a child of a scene that appears at 2s would otherwise
        // have played its entrance at 0 and be sitting still by the time anyone saw it.
        // --cut-start inherits, and animation-delay: var(--cut-start) is how a child uses it.
        const string Html = """
            <div class="scene" data-start="2" data-duration="2">
              <div class="bar"></div>
            </div>
            <style>
              body, html { margin:0; font-family:"Noto Sans"; }
              .scene { position:absolute; left:0; top:0; width:100%; height:100%; background-color:#101010; }
              .bar { position:absolute; left:0; top:0; width:10px; height:100%; background-color:#00ff00;
                     animation: grow 1s linear var(--cut-start) both; }
              @keyframes grow { from { width:10px; } to { width:200px; } }
            </style>
            """;

        using var harness = new Harness();
        var name = harness.WriteComposition("delayed.html", Html);

        // At 2.0 the scene has just appeared and the bar has not grown.
        Assert.NotEqual(new SKColor(0x00, 0xFF, 0x00), PixelAt(harness, name, 2.01, x: 150));
        // By 3.0 the bar has run its full second and reaches past x=150.
        Assert.Equal(new SKColor(0x00, 0xFF, 0x00), PixelAt(harness, name, 3.0, x: 150));
    }

    [Fact]
    public void A_timeline_is_still_pure_in_t()
    {
        // Generated windows are @keyframes like any other, so a composition with a timeline still
        // renders directly rather than being swept - which is most of why this design was chosen.
        using var harness = new Harness();
        var name = harness.WriteComposition("scenes.html", Scenes);
        var loaded = Timeline.Apply(harness.Cut.LoadComposition(name));

        Assert.True(Purity.Analyse(loaded).PureInTime);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static SKColor PixelAt(Harness harness, string name, double t, int x = 40)
    {
        SKColor found = default;
        harness.Cut.Sweep(
            new SweepSpec { Composition = name, Width = 320, Height = 180, Times = [t], Background = SKColors.Black },
            frame =>
            {
                using var bitmap = SKBitmap.FromImage(frame.Image);
                found = bitmap.GetPixel(x, 90);
            });
        return found;
    }

    /// <summary>The stops of one generated keyframes, in order.</summary>
    private static List<(double Percent, double Opacity)> Stops(string css, string name)
    {
        var start = css.IndexOf("@keyframes " + name, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no @keyframes {name} in the generated stylesheet");

        // The block ends at the brace in column one, not at the first inner one.
        var end = css.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, $"@keyframes {name} is not closed");

        var body = css[start..end];
        var stops = new List<(double, double)>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(body, @"([\d.]+)%\s*\{\s*opacity:\s*([\d.]+)"))
        {
            stops.Add((double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                       double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return stops;
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    private static Composition Composition(string path, string html, string? css) =>
        new(path, ".", html, css, [], []);
}
