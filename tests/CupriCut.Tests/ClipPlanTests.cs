using CupriCut.Services;
using Xunit;

namespace CupriCut.Tests;

/// <summary>
/// A clip is whole frames, so its length is quantised to 1/fps. These cover the two things that
/// must always be true: the frame count is the nearest one to what was asked, and the difference
/// is reported rather than absorbed.
/// </summary>
public sealed class ClipPlanTests
{
    [Theory]
    [InlineData(10, 120, 1200)]
    [InlineData(10, 30, 300)]
    [InlineData(10, 24, 240)]
    [InlineData(2, 30, 60)]
    [InlineData(0.5, 120, 60)]
    [InlineData(600, 60, 36000)]     // ten minutes, which the old frame ceiling refused
    public void An_achievable_length_is_exact_and_says_nothing(double seconds, double fps, int frames)
    {
        var (times, plan) = ClipPlanner.Plan(0, seconds, fps);

        Assert.True(plan.Exact);
        Assert.Null(plan.Note);
        Assert.Equal(0, plan.DeltaMs);
        Assert.Equal(frames, plan.Frames);
        Assert.Equal(frames, times.Length);
        Assert.Equal(seconds, plan.ActualSeconds, 9);
        Assert.Equal(seconds, times.Length / fps, 9);
    }

    [Fact]
    public void An_unachievable_length_snaps_to_the_nearest_frame_and_reports_the_delta()
    {
        // 10s at 29.97 fps is 299.7 frames, and there is no such thing as 0.7 of a frame.
        var (_, plan) = ClipPlanner.Plan(0, 10, 29.97);

        Assert.False(plan.Exact);
        Assert.Equal(300, plan.Frames);                     // nearest, not floor
        Assert.Equal(10.01001, plan.ActualSeconds, 5);
        Assert.Equal(10.01, plan.DeltaMs, 2);
        Assert.NotNull(plan.Note);
    }

    [Fact]
    public void The_note_names_a_frame_rate_that_would_be_exact()
    {
        // The fix most people want is the integer rate the fractional one approximates, so say it
        // rather than leaving them to work it out.
        var (_, plan) = ClipPlanner.Plan(0, 10, 29.97);

        Assert.Contains("299.7 frames", plan.Note);
        Assert.Contains("nearest whole frame (300)", plan.Note);
        Assert.Contains("use 30 fps", plan.Note);
    }

    [Fact]
    public void When_no_nearby_rate_would_help_it_names_the_achievable_length_instead()
    {
        // 0.15s at 30 fps is 4.5 frames, and 30 is already the integer rate - so the only advice
        // left is which length would have been exact.
        var (_, plan) = ClipPlanner.Plan(0, 0.15, 30);

        Assert.False(plan.Exact);
        Assert.Equal(5, plan.Frames);
        Assert.DoesNotContain("fps (", plan.Note);
        Assert.Contains("ask for 0.1667s", plan.Note);
    }

    [Fact]
    public void Rounding_goes_to_the_nearest_frame_in_both_directions()
    {
        Assert.Equal(3, ClipPlanner.Plan(0, 0.11, 30).Plan.Frames);   // 3.3 frames, rounds down
        Assert.Equal(3, ClipPlanner.Plan(0, 0.09, 30).Plan.Frames);   // 2.7 frames, rounds up
    }

    [Fact]
    public void A_length_shorter_than_one_frame_still_renders_one_and_explains()
    {
        // Better than an empty sweep or a zero-length file.
        var (times, plan) = ClipPlanner.Plan(0, 0.001, 30);

        Assert.Single(times);
        Assert.Equal(1, plan.Frames);
        Assert.False(plan.Exact);
        Assert.Contains("less than one", plan.Note);
    }

    [Fact]
    public void A_binary_inexact_product_still_rounds_up_to_the_whole_frame_count()
    {
        // 10 * 120 is 1199.9999999999998 in double. Truncating would make the clip a frame short,
        // which is the same class of bug as overrunning.
        Assert.Equal(1199.9999999999998, 10 * 120d, 10);
        Assert.Equal(1200, ClipPlanner.Plan(0, 10, 120).Plan.Frames);
        Assert.True(ClipPlanner.Plan(0, 10, 120).Plan.Exact);
    }

    [Fact]
    public void Frames_cover_the_length_exclusive_of_its_end()
    {
        // The last frame of a 10s clip is at 9.99167s and holds until 10. Including t=10 as well
        // would be 1201 frames and a 10.008s file.
        var (times, plan) = ClipPlanner.Plan(0, 10, 120);

        Assert.Equal(0, times[0]);
        Assert.Equal(10 - 1 / 120d, times[^1], 6);
        Assert.Equal(10, plan.Frames / 120d, 9);
    }

    [Fact]
    public void From_offsets_the_frames_without_changing_the_length()
    {
        var (times, plan) = ClipPlanner.Plan(2, 1, 30);

        Assert.Equal(30, plan.Frames);
        Assert.Equal(2, times[0]);
        Assert.Equal(2 + 29 / 30d, times[^1], 6);
        Assert.True(plan.Exact);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_length_of_zero_or_less_is_refused(double seconds) =>
        Assert.Throws<ArgumentException>(() => ClipPlanner.Plan(0, seconds, 30));

    [Fact]
    public void A_frame_rate_of_zero_or_less_is_refused() =>
        Assert.Throws<ArgumentException>(() => ClipPlanner.Plan(0, 10, 0));
}
