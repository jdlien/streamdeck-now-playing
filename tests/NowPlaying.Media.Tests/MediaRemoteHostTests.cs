using NowPlaying.Media;

namespace NowPlaying.Media.Tests;

/// <summary>
/// The rule that decides whether MediaRemote can still be relied on.
///
/// It exists because "is the process alive" was the wrong question. The helper
/// can deadlock inside MediaRemote and stay up, connected and silent for hours,
/// and asking whether it was running answered yes the whole time -- so the
/// fallback to Music never engaged and the dial stayed frozen on whatever had
/// been playing when it wedged.
/// </summary>
public class MediaRemoteHostTests
{
    [Fact]
    public void A_helper_that_just_spoke_is_answering() =>
        Assert.True(MediaRemoteHost.IsAnswering(running: true, silence: TimeSpan.Zero));

    [Fact]
    public void A_quiet_spell_within_the_limit_is_still_answering() =>
        Assert.True(MediaRemoteHost.IsAnswering(
            running: true,
            silence: MediaRemoteHost.SilenceLimit - TimeSpan.FromSeconds(1)));

    [Fact]
    public void A_live_but_silent_helper_is_not_answering() =>
        Assert.False(MediaRemoteHost.IsAnswering(
            running: true,
            silence: MediaRemoteHost.SilenceLimit + TimeSpan.FromSeconds(1)));

    [Fact]
    public void A_dead_helper_is_not_answering_however_recently_it_spoke() =>
        Assert.False(MediaRemoteHost.IsAnswering(running: false, silence: TimeSpan.Zero));

    /// <summary>
    /// The helper ticks every 15 s, so the limit has to clear several missed
    /// ticks. One slow publish must not cost a restart.
    /// </summary>
    [Fact]
    public void The_limit_allows_for_more_than_one_missed_tick() =>
        Assert.True(MediaRemoteHost.SilenceLimit >= TimeSpan.FromSeconds(45));
}
