using NowPlaying.Device;

namespace NowPlaying.Media.Tests;

/// <summary>
/// How long to wait before asking again for a display that enumerated but whose
/// brightness channel did not answer.
///
/// The regression these exist for: a display got exactly one probe. A DDC read
/// that failed for a moment -- which is the normal state of affairs for the few
/// seconds after a wake, while a monitor's scaler is still coming up -- dropped
/// that monitor from the list until a display reconfiguration or a restart of
/// the plugin. The dial then said "BenQ MA270S not connected" about a screen
/// sitting there switched on, for hours, and the only fix was to restart.
///
/// The delay has to serve two opposite cases with one schedule. A monitor that
/// is merely slow answers within seconds and should be picked up straight away.
/// A monitor that is switched off or on another input will not answer for the
/// rest of the day, and every attempt at one costs a bus timeout.
/// </summary>
public class BrightnessRetryTests
{
    private static TimeSpan Retry(int attempt) => MacDisplayBrightnessService.RetryDelay(attempt);

    /// <summary>The case this was written for: a monitor that is only slow to wake.</summary>
    [Fact]
    public void The_first_retry_comes_quickly_enough_to_catch_a_display_still_waking_up() =>
        Assert.InRange(Retry(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

    [Fact]
    public void A_display_that_keeps_refusing_is_asked_less_and_less_often() =>
        Assert.True(Retry(4) > Retry(1));

    [Fact]
    public void The_delay_never_shortens_as_attempts_pile_up()
    {
        for (var attempt = 1; attempt < 12; attempt++)
        {
            Assert.True(Retry(attempt + 1) >= Retry(attempt), $"attempt {attempt + 1} asked sooner than {attempt}");
        }
    }

    /// <summary>
    /// A monitor switched off is the steady state, and it is the same steady
    /// state as a channel whose polls stop being answered. One rate for both.
    /// </summary>
    [Fact]
    public void It_settles_at_the_rate_a_channel_that_has_gone_quiet_is_polled_at() =>
        Assert.Equal(MacDisplayBrightnessService.PollInterval(readCostsABusTransaction: true, quietPolls: 99), Retry(99));

    /// <summary>Backing off must not depend on the counter stopping anywhere.</summary>
    [Fact]
    public void The_steady_state_holds_however_long_the_display_stays_away() =>
        Assert.Equal(Retry(99), Retry(500_000));

    /// <summary>
    /// Retrying forever is the point: a monitor switched off for the weekend
    /// has to come back on its own, without the plugin being restarted.
    /// </summary>
    [Fact]
    public void There_is_no_attempt_at_which_the_plugin_gives_up() =>
        Assert.True(Retry(int.MaxValue) > TimeSpan.Zero);

    /// <summary>
    /// The early retries are the ones worth reading: they say whether a display
    /// came back by itself after a wake. The steady state is a monitor that is
    /// simply switched off, and a line a minute about it would bury the log.
    /// </summary>
    [Fact]
    public void The_early_retries_are_logged_and_the_steady_state_is_not()
    {
        Assert.True(MacDisplayBrightnessService.RetryIsLogged(1));
        Assert.False(MacDisplayBrightnessService.RetryIsLogged(99));
    }

    /// <summary>Going quiet is the last thing that happens, not something it flips in and out of.</summary>
    [Fact]
    public void Once_it_goes_quiet_it_stays_quiet()
    {
        var quiet = Enumerable.Range(1, 40).Select(MacDisplayBrightnessService.RetryIsLogged).ToList();

        Assert.Equal(quiet.OrderByDescending(logged => logged), quiet);
    }

    /// <summary>Nothing counts attempts from zero, but nothing should trip over it either.</summary>
    [Fact]
    public void A_count_below_one_is_treated_as_the_first_attempt()
    {
        Assert.Equal(Retry(1), Retry(0));
        Assert.Equal(Retry(1), Retry(-3));
    }
}
