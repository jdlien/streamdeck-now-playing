using NowPlaying.Device;

namespace NowPlaying.Media.Tests;

/// <summary>
/// How often each kind of display is re-read to notice a brightness change made
/// somewhere else.
///
/// Two opposite hazards meet here. Apple panels announce their own changes, so
/// polling them buys nothing. DDC/CI panels announce nothing at all, so a poll
/// is the only way the strip can ever follow the monitor's own buttons -- but
/// every DDC read is a bus transaction, and doing that on a schedule to a
/// sleeping monitor is what stopped them ever staying asleep on Windows. The
/// sleep guard lives in the worker; these pin the rates.
/// </summary>
public class BrightnessPollingTests
{
    private static TimeSpan Ddc(int quietPolls = 0) =>
        MacDisplayBrightnessService.PollInterval(readCostsABusTransaction: true, quietPolls);

    private static TimeSpan Local(int quietPolls = 0) =>
        MacDisplayBrightnessService.PollInterval(readCostsABusTransaction: false, quietPolls);

    [Fact]
    public void A_ddc_display_is_polled_often_enough_to_feel_current() =>
        Assert.InRange(Ddc(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    [Fact]
    public void A_display_that_announces_its_own_changes_is_only_a_backstop() =>
        Assert.True(Local() >= TimeSpan.FromSeconds(20));

    [Fact]
    public void The_backstop_is_far_less_often_than_the_ddc_poll() =>
        Assert.True(Local() > Ddc());

    [Fact]
    public void A_display_that_stops_answering_is_asked_far_less_often() =>
        Assert.True(Ddc(quietPolls: 5) > Ddc() * 4);

    /// <summary>One missed reply is a moody bus, not an absent monitor.</summary>
    [Fact]
    public void A_single_unanswered_poll_does_not_trigger_the_backoff() =>
        Assert.Equal(Ddc(), Ddc(quietPolls: 1));

    /// <summary>Backing off must not depend on the counter stopping anywhere.</summary>
    [Fact]
    public void The_backoff_holds_however_long_the_display_stays_quiet() =>
        Assert.Equal(Ddc(quietPolls: 5), Ddc(quietPolls: 5000));
}
