using NowPlaying.Media;

namespace NowPlaying.Media.Tests;

/// <summary>
/// Which snapshot changes raise the service's event (README section 6.3).
/// Apple Music refreshes its timeline twice a second; those must not become
/// pushes, while seeks, state changes, and text changes must.
/// </summary>
public class SignificantChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static NowPlayingSnapshot At(double positionSeconds, double ageSeconds, PlaybackState state = PlaybackState.Playing) =>
        new("app", state, "Title", "Artist", TimeSpan.FromSeconds(positionSeconds), Now - TimeSpan.FromSeconds(ageSeconds),
            TimeSpan.FromSeconds(300), true, true, true);

    [Fact]
    public void ARoutineTimelineRefreshIsNotSignificant()
    {
        // Reported 100 s two seconds ago, then 102 s just now: the same place.
        Assert.False(SnapshotChange.IsSignificant(At(100, 2), At(102, 0), Now));
        // Half-second cadence with whole-second positions wobbles by less than a second.
        Assert.False(SnapshotChange.IsSignificant(At(100, 0.5), At(100, 0), Now));
    }

    [Fact]
    public void ASeekIsSignificant()
    {
        Assert.True(SnapshotChange.IsSignificant(At(100, 1), At(150, 0), Now));
        Assert.True(SnapshotChange.IsSignificant(At(100, 1), At(30, 0), Now));
    }

    [Fact]
    public void AStateChangeIsSignificant()
    {
        Assert.True(SnapshotChange.IsSignificant(At(100, 0), At(100, 0, PlaybackState.Paused), Now));
    }

    [Fact]
    public void ATextOrDurationOrControlChangeIsSignificant()
    {
        Assert.True(SnapshotChange.IsSignificant(At(100, 0), At(100, 0) with { Title = "Other" }, Now));
        Assert.True(SnapshotChange.IsSignificant(At(100, 0), At(100, 0) with { Duration = TimeSpan.FromSeconds(301) }, Now));
        Assert.True(SnapshotChange.IsSignificant(At(100, 0), At(100, 0) with { CanNext = false }, Now));
    }

    [Fact]
    public void GainingOrLosingATimelineIsSignificant()
    {
        Assert.True(SnapshotChange.IsSignificant(At(100, 0), At(100, 0) with { Position = null, Duration = null }, Now));
        Assert.True(SnapshotChange.IsSignificant(NowPlayingSnapshot.Empty, At(0, 0), Now));
    }
}
