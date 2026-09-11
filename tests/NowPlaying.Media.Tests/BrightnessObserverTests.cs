using NowPlaying.Device;

namespace NowPlaying.Media.Tests;

/// <summary>
/// Which displays to register for brightness-change notifications after a
/// rebind.
///
/// The regression these exist for: a registration is keyed inside
/// DisplayServices by (display, context), and a rebind builds replacement
/// channels for the same displays before retiring the old ones. While each
/// channel registered on construction and unregistered on disposal, the retired
/// channel's unregister cancelled its replacement's registration -- same
/// display, same context. Apple panels then stopped following changes made
/// anywhere else, from the first rebind until the plugin was restarted, and a
/// rebind happens on every wake and every display reconfiguration. It looked
/// exactly like the feature had never worked, and it worked perfectly from a
/// fresh start.
///
/// So the interesting case here is the one where nothing should happen.
/// </summary>
public class BrightnessObserverTests
{
    [Fact]
    public void A_rebind_that_finds_the_same_displays_changes_nothing()
    {
        var (register, unregister) = MacDisplayBrightnessService.ObserverChanges([3u, 5u], [3u, 5u]);

        Assert.Empty(register);
        Assert.Empty(unregister);
    }

    [Fact]
    public void Order_is_not_identity()
    {
        var (register, unregister) = MacDisplayBrightnessService.ObserverChanges([5u, 3u], [3u, 5u]);

        Assert.Empty(register);
        Assert.Empty(unregister);
    }

    [Fact]
    public void A_display_that_appeared_is_registered()
    {
        var (register, unregister) = MacDisplayBrightnessService.ObserverChanges([3u, 5u, 7u], [3u, 5u]);

        Assert.Equal([7u], register);
        Assert.Empty(unregister);
    }

    [Fact]
    public void A_display_that_went_away_is_dropped()
    {
        var (register, unregister) = MacDisplayBrightnessService.ObserverChanges([3u], [3u, 5u]);

        Assert.Empty(register);
        Assert.Equal([5u], unregister);
    }

    [Fact]
    public void A_display_swapped_for_another_is_both()
    {
        var (register, unregister) = MacDisplayBrightnessService.ObserverChanges([3u, 9u], [3u, 5u]);

        Assert.Equal([9u], register);
        Assert.Equal([5u], unregister);
    }

    /// <summary>Shutting down asks for nothing, which must drop every registration.</summary>
    [Fact]
    public void Wanting_none_drops_them_all()
    {
        var (register, unregister) = MacDisplayBrightnessService.ObserverChanges([], [3u, 5u]);

        Assert.Empty(register);
        Assert.Equal([3u, 5u], unregister);
    }

    /// <summary>A DDC-only setup registers nothing and has nothing to drop.</summary>
    [Fact]
    public void Starting_with_no_displays_that_announce_is_a_no_op()
    {
        var (register, unregister) = MacDisplayBrightnessService.ObserverChanges([], []);

        Assert.Empty(register);
        Assert.Empty(unregister);
    }
}
