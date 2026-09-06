using NowPlaying.Media;

namespace NowPlaying.Media.Tests;

public class TimelineMathTests
{
    private static readonly DateTimeOffset Recent = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void PositionAndDurationAreRelativeToStart()
    {
        var t = TimelineMath.Normalize(Sec(10), Sec(250), Sec(40), Recent);
        Assert.Equal(Sec(30), t.Position);
        Assert.Equal(Sec(240), t.Duration);
        Assert.Equal(Recent, t.PositionAt);
    }

    /// <summary>foobar2000 reports 0/0/0 with a zero LastUpdatedTime even while playing.</summary>
    [Fact]
    public void AnEmptyTimelineHasNoDurationAndNoPosition()
    {
        var t = TimelineMath.Normalize(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimelineMath.WinRtEpoch);
        Assert.Null(t.Duration);
        Assert.Null(t.Position);
        Assert.Null(t.PositionAt);
    }

    [Fact]
    public void ABackwardsTimelineHasNoDuration()
    {
        var t = TimelineMath.Normalize(Sec(300), Sec(10), Sec(300), Recent);
        Assert.Null(t.Duration);
    }

    [Fact]
    public void AnAbsurdDurationIsNotUsable()
    {
        var t = TimelineMath.Normalize(TimeSpan.Zero, TimeSpan.FromHours(24), TimeSpan.Zero, Recent);
        Assert.Null(t.Duration);

        var podcast = TimelineMath.Normalize(TimeSpan.Zero, TimeSpan.FromHours(3), TimeSpan.Zero, Recent);
        Assert.Equal(TimeSpan.FromHours(3), podcast.Duration);
    }

    [Fact]
    public void APositionOutsideTheTrackIsClamped()
    {
        Assert.Equal(TimeSpan.Zero, TimelineMath.Normalize(Sec(50), Sec(300), Sec(10), Recent).Position);
        Assert.Equal(Sec(250), TimelineMath.Normalize(Sec(50), Sec(300), Sec(900), Recent).Position);
    }

    [Fact]
    public void TheWinRtEpochMeansNeverUpdated()
    {
        Assert.Null(TimelineMath.Normalize(Sec(0), Sec(100), Sec(10), TimelineMath.WinRtEpoch).PositionAt);
        Assert.Equal(Recent, TimelineMath.Normalize(Sec(0), Sec(100), Sec(10), Recent).PositionAt);
    }

    /// <summary>
    /// The off-by-one the ak820-pro audit found: converting each endpoint to
    /// floating-point seconds and subtracting gives 244.99999999999997 for a
    /// 245-second track starting at 11.001 s. Tick arithmetic gives 245.
    /// </summary>
    [Fact]
    public void AnOrdinaryTrackDoesNotLoseASecondToFloatingPoint()
    {
        var start = TimeSpan.FromTicks(110_010_000); // 11.001 s
        var end = start + Sec(245);
        var t = TimelineMath.Normalize(start, end, start, Recent);
        Assert.Equal(245, (int)t.Duration!.Value.TotalSeconds);
        Assert.Equal(Sec(245), t.Duration);

        // The float path really would have lost it; keep the witness.
        var asFloat = end.Ticks / 1e7 - start.Ticks / 1e7;
        Assert.Equal(244, (int)asFloat);
    }
}
