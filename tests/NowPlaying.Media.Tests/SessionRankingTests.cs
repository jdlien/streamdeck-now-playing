using NowPlaying.Media;

namespace NowPlaying.Media.Tests;

public class SessionRankingTests
{
    private static SessionFacts S(string app, SessionStatus status, bool current = false) => new(app, status, current);

    [Fact]
    public void NothingAtAllChoosesNothing()
    {
        Assert.Null(SessionRanking.Choose([]));
    }

    [Theory]
    [InlineData(SessionStatus.Closed)]
    [InlineData(SessionStatus.Opened)]
    [InlineData(SessionStatus.Changing)]
    [InlineData(SessionStatus.Unreadable)]
    public void OnlyPlayingPausedAndStoppedAreCandidates(SessionStatus status)
    {
        Assert.Null(SessionRanking.Rank(status));
        Assert.Null(SessionRanking.Choose([S("app", status, current: true)]));
    }

    /// <summary>
    /// The regression the ranking exists for (from ak820-pro): Windows called
    /// Apple Music, merely open, the current session while foobar2000 held
    /// the track.
    /// </summary>
    [Fact]
    public void APlayingSessionBeatsTheCurrentOneThatIsPaused()
    {
        var chosen = SessionRanking.Choose([
            S("AppleMusic", SessionStatus.Paused, current: true),
            S("foobar2000.exe", SessionStatus.Playing),
        ]);
        Assert.Equal("foobar2000.exe", chosen!.AppId);
    }

    [Fact]
    public void AnOpenedCurrentSessionNeverWins()
    {
        var chosen = SessionRanking.Choose([
            S("AppleMusic", SessionStatus.Opened, current: true),
            S("foobar2000.exe", SessionStatus.Paused),
        ]);
        Assert.Equal("foobar2000.exe", chosen!.AppId);
    }

    [Fact]
    public void TheCurrentSessionWinsAmongEquals()
    {
        Assert.Equal("Spotify", SessionRanking.Choose([
            S("Chrome", SessionStatus.Playing),
            S("Spotify", SessionStatus.Playing, current: true),
        ])!.AppId);

        Assert.Equal("Spotify", SessionRanking.Choose([
            S("Chrome", SessionStatus.Paused),
            S("Spotify", SessionStatus.Paused, current: true),
        ])!.AppId);
    }

    [Fact]
    public void TiesKeepTheManagersOrdering()
    {
        var chosen = SessionRanking.Choose([
            S("first", SessionStatus.Playing),
            S("second", SessionStatus.Playing),
        ]);
        Assert.Equal("first", chosen!.AppId);
    }

    /// <summary>foobar2000's Stop button leaves a loaded track reported as Stopped.</summary>
    [Fact]
    public void StoppedIsACandidateBelowPaused()
    {
        Assert.Equal(2, SessionRanking.Rank(SessionStatus.Stopped));

        var alone = SessionRanking.Choose([S("foobar2000.exe", SessionStatus.Stopped)]);
        Assert.Equal("foobar2000.exe", alone!.AppId);

        var withPaused = SessionRanking.Choose([
            S("foobar2000.exe", SessionStatus.Stopped, current: true),
            S("AppleMusic", SessionStatus.Paused),
        ]);
        Assert.Equal("AppleMusic", withPaused!.AppId);
    }

    [Fact]
    public void APreferredAppWinsWheneverItIsACandidate()
    {
        var chosen = SessionRanking.Choose(
            [
                S("Chrome", SessionStatus.Playing, current: true),
                S("Spotify", SessionStatus.Paused),
            ],
            preferredAppId: "Spotify");
        Assert.Equal("Spotify", chosen!.AppId);
    }

    [Fact]
    public void APreferredAppThatIsNotACandidateIsIgnored()
    {
        var chosen = SessionRanking.Choose(
            [
                S("Chrome", SessionStatus.Playing),
                S("Spotify", SessionStatus.Opened),
            ],
            preferredAppId: "Spotify");
        Assert.Equal("Chrome", chosen!.AppId);
    }
}
