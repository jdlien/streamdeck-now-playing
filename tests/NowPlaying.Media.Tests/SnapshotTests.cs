using NowPlaying.Media;

namespace NowPlaying.Media.Tests;

public class SnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static NowPlayingSnapshot Playing(double positionSeconds, double ageSeconds, double durationSeconds = 300) =>
        new(
            "app",
            PlaybackState.Playing,
            "Title",
            "Artist",
            TimeSpan.FromSeconds(positionSeconds),
            Now - TimeSpan.FromSeconds(ageSeconds),
            TimeSpan.FromSeconds(durationSeconds),
            true,
            true,
            true);

    [Fact]
    public void AStalePositionIsExtrapolatedWhilePlaying()
    {
        Assert.Equal(TimeSpan.FromSeconds(104.7), Playing(100, 4.7).EffectivePosition(Now));
    }

    [Fact]
    public void APausedPositionIsNeverExtrapolated()
    {
        var paused = Playing(100, 120) with { State = PlaybackState.Paused };
        Assert.Equal(TimeSpan.FromSeconds(100), paused.EffectivePosition(Now));

        var stopped = paused with { State = PlaybackState.Stopped };
        Assert.Equal(TimeSpan.FromSeconds(100), stopped.EffectivePosition(Now));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-3600)]
    [InlineData(600)]
    [InlineData(1e9)]
    public void AnImplausibleAgeIsNotTrusted(double age)
    {
        Assert.Equal(TimeSpan.FromSeconds(100), Playing(100, age, durationSeconds: 100_000).EffectivePosition(Now));
    }

    [Fact]
    public void TheTrustedBoundariesAreInclusiveAtZeroAndExclusiveAtTenMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(100), Playing(100, 0, durationSeconds: 1000).EffectivePosition(Now));
        Assert.Equal(TimeSpan.FromSeconds(699.9), Playing(100, 599.9, durationSeconds: 1000).EffectivePosition(Now));
    }

    [Fact]
    public void ExtrapolationCannotRunPastTheEndOfTheTrack()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), Playing(118, 30, durationSeconds: 120).EffectivePosition(Now));
    }

    [Fact]
    public void NoReportTimeMeansNoExtrapolation()
    {
        var snapshot = Playing(100, 30) with { PositionAt = null };
        Assert.Equal(TimeSpan.FromSeconds(100), snapshot.EffectivePosition(Now));
    }

    [Fact]
    public void NoPositionMeansNoPosition()
    {
        var snapshot = Playing(100, 30) with { Position = null };
        Assert.Null(snapshot.EffectivePosition(Now));
        Assert.Null(NowPlayingSnapshot.Empty.EffectivePosition(Now));
    }

    [Fact]
    public void SnapshotsCompareByValue()
    {
        Assert.Equal(Playing(1, 2), Playing(1, 2));
        Assert.NotEqual(Playing(1, 2), Playing(1, 2) with { Title = "Other" });
    }
}
