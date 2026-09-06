using NowPlaying.Media;

namespace NowPlaying.Media.Tests;

public class ResumeCorrectorTests
{
    private static readonly DateTimeOffset PausedAt = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ResumedAt = PausedAt + TimeSpan.FromMinutes(5);

    private static NowPlayingSnapshot Snapshot(PlaybackState state, DateTimeOffset? reportedAt, string app = "app") =>
        new(app, state, "Title", "Artist", TimeSpan.FromSeconds(209), reportedAt, TimeSpan.FromSeconds(255), true, true, true);

    /// <summary>The live glitch: paused at 3:29, resumed five minutes later, first snapshot said 4:15.</summary>
    [Fact]
    public void ResumingReplacesTheStaleReportTimeWithTheResumeMoment()
    {
        var corrector = new ResumeCorrector();
        var paused = Snapshot(PlaybackState.Paused, PausedAt);
        var resumedRaw = Snapshot(PlaybackState.Playing, PausedAt);

        var corrected = corrector.Apply(paused, resumedRaw, ResumedAt);

        Assert.Equal(ResumedAt, corrected.PositionAt);
        Assert.Equal(TimeSpan.FromSeconds(209), corrected.EffectivePosition(ResumedAt));
        Assert.Equal(TimeSpan.FromSeconds(212), corrected.EffectivePosition(ResumedAt + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void TheCorrectionHoldsUntilThePlayerReportsAFreshTimeline()
    {
        var corrector = new ResumeCorrector();
        var paused = Snapshot(PlaybackState.Paused, PausedAt);
        var first = corrector.Apply(paused, Snapshot(PlaybackState.Playing, PausedAt), ResumedAt);

        // Same stale report again: still corrected.
        var again = corrector.Apply(first, Snapshot(PlaybackState.Playing, PausedAt), ResumedAt + TimeSpan.FromSeconds(1));
        Assert.Equal(ResumedAt, again.PositionAt);

        // Fresh report from the player: passed through untouched.
        var fresh = ResumedAt + TimeSpan.FromSeconds(2);
        var passed = corrector.Apply(again, Snapshot(PlaybackState.Playing, fresh), fresh);
        Assert.Equal(fresh, passed.PositionAt);

        // And the stale value does not come back if it is seen later.
        var later = corrector.Apply(passed, Snapshot(PlaybackState.Playing, PausedAt), fresh + TimeSpan.FromSeconds(1));
        Assert.Equal(PausedAt, later.PositionAt);
    }

    [Fact]
    public void APlayerAlreadyPlayingWhenFirstSeenIsNotCorrected()
    {
        var corrector = new ResumeCorrector();
        var raw = Snapshot(PlaybackState.Playing, PausedAt);
        Assert.Equal(PausedAt, corrector.Apply(NowPlayingSnapshot.Empty, raw, ResumedAt).PositionAt);
    }

    [Fact]
    public void SwitchingToADifferentPlayingSessionIsNotCorrected()
    {
        var corrector = new ResumeCorrector();
        var previous = Snapshot(PlaybackState.Paused, PausedAt, app: "one");
        var raw = Snapshot(PlaybackState.Playing, PausedAt, app: "two");
        Assert.Equal(PausedAt, corrector.Apply(previous, raw, ResumedAt).PositionAt);
    }

    [Fact]
    public void PausingClearsTheCorrection()
    {
        var corrector = new ResumeCorrector();
        var paused = Snapshot(PlaybackState.Paused, PausedAt);
        corrector.Apply(paused, Snapshot(PlaybackState.Playing, PausedAt), ResumedAt);

        var pausedAgain = corrector.Apply(Snapshot(PlaybackState.Playing, ResumedAt), Snapshot(PlaybackState.Paused, PausedAt), ResumedAt + TimeSpan.FromSeconds(5));
        Assert.Equal(PausedAt, pausedAgain.PositionAt);
    }
}
