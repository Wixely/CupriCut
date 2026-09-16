using System.Text.Json;
using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// <c>data-cut-event</c>: a time and a name, read out of the markup and written back out beside a
/// render. Nothing here executes anything, which is the whole design - see Services/Events.
/// </summary>
public class EventTests
{
    [Fact]
    public void A_time_and_a_name_come_back_as_one_event()
    {
        var plan = Events.Plan("""<div class="bar" data-cut-event="2.4:bar-full"></div>""");

        var e = Assert.Single(plan.Events);
        Assert.Equal(2.4, e.At);
        Assert.Equal("bar-full", e.Name);
        Assert.Equal("div", e.Tag);
        Assert.Equal("bar", e.Classes);
        Assert.False(e.Relative);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void A_composition_that_declares_nothing_costs_nothing()
    {
        // The attribute is looked for in the string before anything is parsed, because every
        // composition that has never heard of events still goes through here on every inspect.
        var plan = Events.Plan("""<div class="bar"><span>hello</span></div>""");

        Assert.False(plan.Any);
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void A_plus_makes_the_time_relative_to_the_elements_own_start()
    {
        // The point of the relative form. Inside a scene that begins at 6s, an author thinks in
        // "1.2s after this appears", not in "7.2s into the film" - and if the scene moves, the
        // absolute version is quietly wrong while this one is not.
        var plan = Events.Plan(
            """<div class="scene" data-start="6" data-duration="4" data-cut-event="+1.2:boost-landed"></div>""");

        var e = Assert.Single(plan.Events);
        Assert.Equal(7.2, e.At, 6);
        Assert.True(e.Relative);
    }

    [Fact]
    public void On_an_element_with_no_start_the_two_forms_mean_the_same_thing()
    {
        var absolute = Events.Plan("""<div data-cut-event="1.5:a"></div>""").Events[0];
        var relative = Events.Plan("""<div data-cut-event="+1.5:a"></div>""").Events[0];

        Assert.Equal(absolute.At, relative.At);
    }

    [Fact]
    public void Several_events_share_one_attribute()
    {
        var plan = Events.Plan(
            """<div data-start="2" data-cut-event="+0.5:in; +2:out; 9:last"></div>""");

        Assert.Equal(["in", "out", "last"], plan.Events.Select(e => e.Name));
        Assert.Equal([2.5, 4, 9], plan.Events.Select(e => e.At));
    }

    [Fact]
    public void Events_come_back_in_time_order_whatever_order_they_were_written_in()
    {
        // Whatever reads the sidecar is walking a clock forwards. Making it sort first would be
        // asking every caller to do the same work.
        var plan = Events.Plan("""
            <div class="late" data-cut-event="8:third"></div>
            <div class="early" data-cut-event="1:first"></div>
            <div class="mid" data-cut-event="4:second"></div>
            """);

        Assert.Equal(["first", "second", "third"], plan.Events.Select(e => e.Name));
    }

    [Fact]
    public void The_track_comes_along_when_there_is_one()
    {
        var plan = Events.Plan(
            """<div class="scene" data-track="card" data-start="6" data-cut-event="+1:x"></div>""");

        Assert.Equal("card", Assert.Single(plan.Events).Track);
    }

    [Fact]
    public void The_generated_timeline_class_is_not_reported_as_the_authors_own()
    {
        // Plan runs on the markup either side of the timeline rewrite. cut-t3 means nothing to
        // whoever reads the report, and reporting it would suggest they had written it.
        var plan = Events.Plan("""<div class="scene cut-t3" data-cut-event="1:x"></div>""");

        Assert.Equal("scene", Assert.Single(plan.Events).Classes);
    }

    [Theory]
    [InlineData("""<div data-cut-event="notatime:x"></div>""", "not a number")]
    [InlineData("""<div data-cut-event="2.4"></div>""", "not a time and a name")]
    [InlineData("""<div data-cut-event="2.4:"></div>""", "not a time and a name")]
    [InlineData("""<div data-cut-event="-1:x"></div>""", "before the film starts")]
    public void A_mark_that_cannot_be_read_is_said_out_loud(string html, string expected)
    {
        // Silence would mean a mark that simply never appears in the sidecar, and an author who
        // finds out when the thing that was supposed to fire does not.
        var plan = Events.Plan(html);

        Assert.Empty(plan.Events);
        Assert.Contains(expected, Assert.Single(plan.Problems));
    }

    [Fact]
    public void One_bad_mark_does_not_take_the_good_ones_with_it()
    {
        var plan = Events.Plan("""<div data-cut-event="1:good; wrong; 3:alsogood"></div>""");

        Assert.Equal(["good", "alsogood"], plan.Events.Select(e => e.Name));
        Assert.Single(plan.Problems);
    }

    [Theory]
    [InlineData(2.4, 30, 72)]
    [InlineData(2.4, 25, 60)]
    [InlineData(0, 30, 0)]
    [InlineData(1.0 / 60, 30, 1)]   // rounds to nearest, not down: half a frame at 30 is frame 1
    public void A_time_becomes_the_frame_it_lands_on(double at, double fps, int frame)
    {
        var e = new CutEvent(at, "x", "div", null, null, false);

        Assert.Equal(frame, e.Frame(fps));
    }

    [Fact]
    public void A_render_leaves_the_events_beside_its_output()
    {
        const string Html = """
            <div class="scene" data-track="card" data-start="6" data-cut-event="+2.4:boost-landed"></div>
            <div class="bar" data-cut-event="1.5:bar-full"></div>
            """;

        using var harness = new Harness();
        var directory = Path.Combine(harness.Root, "output", "run");

        var path = Events.WriteSidecar(Html, "fixture.html", directory, "fixture_mp4", fps: 30, duration: 12);

        Assert.NotNull(path);
        Assert.Equal("fixture_mp4.events.json", Path.GetFileName(path));

        var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path!));
        Assert.Equal("fixture.html", json.GetProperty("composition").GetString());
        Assert.Equal(30, json.GetProperty("fps").GetDouble());

        var events = json.GetProperty("events");
        Assert.Equal(2, events.GetArrayLength());

        Assert.Equal("bar-full", events[0].GetProperty("name").GetString());
        Assert.Equal(45, events[0].GetProperty("frame").GetInt32());
        Assert.Equal("div.bar", events[0].GetProperty("element").GetString());

        Assert.Equal("boost-landed", events[1].GetProperty("name").GetString());
        Assert.Equal(8.4, events[1].GetProperty("at").GetDouble(), 6);
        Assert.Equal(252, events[1].GetProperty("frame").GetInt32());
        Assert.Equal("card", events[1].GetProperty("track").GetString());
    }

    [Fact]
    public void An_event_after_the_last_frame_is_marked_in_the_file()
    {
        // Not an error - a clip is often a window onto a longer composition. But a frame number
        // nothing will ever reach, written plainly next to ones that will, is a trap.
        using var harness = new Harness();
        var directory = Path.Combine(harness.Root, "output", "run");

        var path = Events.WriteSidecar(
            """<div data-cut-event="1:inside; 9:outside"></div>""",
            "c.html", directory, "c", fps: 30, duration: 4);

        var events = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path!)).GetProperty("events");

        Assert.False(events[0].TryGetProperty("pastTheEnd", out _));
        Assert.True(events[1].GetProperty("pastTheEnd").GetBoolean());
    }

    [Fact]
    public void Nothing_declared_means_no_file_at_all()
    {
        // A directory of renders should not fill up with empty sidecars.
        using var harness = new Harness();
        var directory = Path.Combine(harness.Root, "output", "run");
        Directory.CreateDirectory(directory);

        Assert.Null(Events.WriteSidecar("<div class=\"a\"></div>", "c.html", directory, "c", 30, 4));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }
}
