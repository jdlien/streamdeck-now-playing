using NowPlaying.Media;
using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

public class FeedbackRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The Apple Music session captured on 2026-09-06, paused at 3:07 of 3:55.</summary>
    private static readonly NowPlayingSnapshot AppleMusicPaused = new(
        "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
        PlaybackState.Paused,
        "Warriors of the Wasteland",
        "Michael Oakley — Prologue",
        TimeSpan.FromSeconds(187),
        Now - TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(235),
        true,
        true,
        true);

    /// <summary>foobar2000 playing: metadata but no timeline at all.</summary>
    private static readonly NowPlayingSnapshot FoobarPlaying = new(
        "foobar2000.exe",
        PlaybackState.Playing,
        "Remember (ESCM 12' Mix)",
        "BT",
        null,
        null,
        null,
        true,
        true,
        true);

    [Fact]
    public void NoMediaHidesTheIconTheBarAndTheTimes()
    {
        var frame = FeedbackRenderer.Render(NowPlayingSnapshot.Empty, Now);
        Assert.Equal(new FeedbackFrame(null, "No media", "", false, 0, "", ""), frame);

        var payload = FeedbackRenderer.Diff(null, frame);
        Assert.Equal(6, payload.Count);
        var icon = Assert.IsType<Dictionary<string, object>>(payload["icon"]);
        Assert.Equal(false, icon["enabled"]);
        Assert.False(icon.ContainsKey("value"));
        var progress = Assert.IsType<Dictionary<string, object>>(payload["progress"]);
        Assert.Equal(false, progress["enabled"]);
        Assert.Equal("", payload["elapsed"]);
        Assert.Equal("", payload["total"]);
    }

    [Fact]
    public void APausedTrackShowsThePauseIconAFrozenBarAndTheTimes()
    {
        var frame = FeedbackRenderer.Render(AppleMusicPaused, Now);
        Assert.Equal(FeedbackRenderer.PauseIcon, frame.Icon);
        Assert.Equal("Warriors of the Wasteland", frame.Track);
        Assert.Equal("Michael Oakley — Prologue", frame.Artist);
        Assert.True(frame.BarEnabled);
        Assert.Equal(796, frame.BarValue); // 187 / 235 of 1000, rounded
        Assert.Equal("3:07", frame.Elapsed);
        Assert.Equal("3:55", frame.Total);
    }

    [Fact]
    public void APlayingTrackAdvancesTheBarAndTheElapsedTimeByTheAgeOfTheReport()
    {
        var playing = AppleMusicPaused with { State = PlaybackState.Playing, PositionAt = Now - TimeSpan.FromSeconds(10) };
        var frame = FeedbackRenderer.Render(playing, Now);
        Assert.Equal(FeedbackRenderer.PlayIcon, frame.Icon);
        Assert.Equal(838, frame.BarValue); // 197 / 235 of 1000, rounded
        Assert.Equal("3:17", frame.Elapsed);
        Assert.Equal("3:55", frame.Total);
    }

    [Fact]
    public void StoppedShowsThePauseIcon()
    {
        Assert.Equal(FeedbackRenderer.PauseIcon, FeedbackRenderer.Render(AppleMusicPaused with { State = PlaybackState.Stopped }, Now).Icon);
    }

    [Fact]
    public void NoDurationHidesTheBarAndTimesButKeepsTheText()
    {
        var frame = FeedbackRenderer.Render(FoobarPlaying, Now);
        Assert.Equal(FeedbackRenderer.PlayIcon, frame.Icon);
        Assert.Equal("Remember (ESCM 12' Mix)", frame.Track);
        Assert.Equal("BT", frame.Artist);
        Assert.False(frame.BarEnabled);
        Assert.Equal("", frame.Elapsed);
        Assert.Equal("", frame.Total);
    }

    [Fact]
    public void ASessionWithNoTextShowsTheAppName()
    {
        var frame = FeedbackRenderer.Render(FoobarPlaying with { Title = "", Artist = "" }, Now);
        Assert.Equal("foobar2000", frame.Track);
        Assert.Equal("", frame.Artist);
    }

    [Fact]
    public void OnlyAnArtistMovesUpToTheFirstLine()
    {
        var frame = FeedbackRenderer.Render(FoobarPlaying with { Title = "" }, Now);
        Assert.Equal("BT", frame.Track);
        Assert.Equal("", frame.Artist);
    }

    [Fact]
    public void AnUnchangedFrameSendsNothing()
    {
        var frame = FeedbackRenderer.Render(AppleMusicPaused, Now);
        Assert.Empty(FeedbackRenderer.Diff(frame, frame));
    }

    [Fact]
    public void OnlyTheProgressItemsAreSentWhenOnlyThePositionMoved()
    {
        var playing = AppleMusicPaused with { State = PlaybackState.Playing, PositionAt = Now - TimeSpan.FromSeconds(10) };
        var before = FeedbackRenderer.Render(playing, Now);
        var after = FeedbackRenderer.Render(playing, Now + TimeSpan.FromSeconds(5));
        Assert.Equal(838, before.BarValue); // 197 / 235
        Assert.Equal(860, after.BarValue);  // 202 / 235
        var payload = FeedbackRenderer.Diff(before, after);
        Assert.Equal(new[] { "elapsed", "progress" }, payload.Keys.Order());
        var progress = Assert.IsType<Dictionary<string, object>>(payload["progress"]);
        Assert.Equal(true, progress["enabled"]);
        Assert.Equal(860, progress["value"]);
        Assert.Equal("3:22", payload["elapsed"]);
    }

    [Fact]
    public void WithinTheSameSecondOnlyTheBarMoves()
    {
        var playing = AppleMusicPaused with { State = PlaybackState.Playing, PositionAt = Now - TimeSpan.FromSeconds(10) };
        var before = FeedbackRenderer.Render(playing, Now);
        var after = FeedbackRenderer.Render(playing, Now + TimeSpan.FromMilliseconds(400));
        var payload = FeedbackRenderer.Diff(before, after);
        Assert.False(payload.ContainsKey("elapsed"));
    }

    [Fact]
    public void AFullPushCarriesEveryItem()
    {
        var payload = FeedbackRenderer.Diff(null, FeedbackRenderer.Render(AppleMusicPaused, Now));
        Assert.Equal(new[] { "artist", "elapsed", "icon", "progress", "total", "track" }, payload.Keys.Order());
        var icon = Assert.IsType<Dictionary<string, object>>(payload["icon"]);
        Assert.Equal(FeedbackRenderer.PauseIcon, icon["value"]);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9, "0:09")]
    [InlineData(65, "1:05")]
    [InlineData(235, "3:55")]
    [InlineData(3599.9, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3665, "1:01:05")]
    [InlineData(-5, "0:00")]
    public void ClockFormatsMinutesAndHours(double seconds, string expected)
    {
        Assert.Equal(expected, FeedbackRenderer.Clock(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("foobar2000.exe", "foobar2000")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "AppleMusicWin")]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "SpotifyMusic")]
    [InlineData("MSEdge", "MSEdge")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void AppDisplayNamesAreReadable(string? appId, string expected)
    {
        Assert.Equal(expected, FeedbackRenderer.AppDisplayName(appId));
    }
}
