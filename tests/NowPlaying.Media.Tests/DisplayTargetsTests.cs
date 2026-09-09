using NowPlaying.Device;
using NowPlaying.Plugin;

namespace NowPlaying.Media.Tests;

public class DisplayTargetsTests
{
    private static readonly string[] Two = ["Odyssey G95NC", "SF10T"];
    private static readonly string[] One = ["Odyssey G95NC"];
    private static readonly string[] None = [];

    [Fact]
    public void TheDeckComesLastWhenIncluded()
    {
        Assert.Equal(["Odyssey G95NC", "SF10T", DisplayTargets.StreamDeck], DisplayTargets.Order(Two, true));
        Assert.Equal(["Odyssey G95NC", "SF10T"], DisplayTargets.Order(Two, false));
        Assert.Equal([DisplayTargets.StreamDeck], DisplayTargets.Order(None, true));
    }

    [Fact]
    public void AutomaticMeansThePrimaryThenTheDeckThenNothing()
    {
        Assert.Equal("Odyssey G95NC", DisplayTargets.Resolve("", Two, true));
        Assert.Equal(DisplayTargets.StreamDeck, DisplayTargets.Resolve("", None, true));
        Assert.Null(DisplayTargets.Resolve("", None, false));
    }

    [Fact]
    public void AnExplicitDeckBindingIsAlwaysHonoured()
    {
        Assert.Equal(DisplayTargets.StreamDeck, DisplayTargets.Resolve(DisplayTargets.StreamDeck, Two, false));
    }

    [Fact]
    public void ANamedMonitorResolvesCaseInsensitivelyOrNotAtAll()
    {
        Assert.Equal("SF10T", DisplayTargets.Resolve("sf10t", Two, true));
        Assert.Null(DisplayTargets.Resolve("DELL U2723QE", Two, true));
    }

    [Fact]
    public void CyclingWrapsThroughMonitorsAndTheDeck()
    {
        Assert.Equal("SF10T", DisplayTargets.Neighbor("", Two, true, +1));
        Assert.Equal(DisplayTargets.StreamDeck, DisplayTargets.Neighbor("SF10T", Two, true, +1));
        Assert.Equal("Odyssey G95NC", DisplayTargets.Neighbor(DisplayTargets.StreamDeck, Two, true, +1));
        Assert.Equal(DisplayTargets.StreamDeck, DisplayTargets.Neighbor("", Two, true, -1));
        Assert.Equal("SF10T", DisplayTargets.Neighbor(DisplayTargets.StreamDeck, Two, true, -1));
    }

    [Fact]
    public void CyclingSkipsTheDeckWhenExcluded()
    {
        Assert.Equal("Odyssey G95NC", DisplayTargets.Neighbor("SF10T", Two, false, +1));
        Assert.Equal("SF10T", DisplayTargets.Neighbor("", Two, false, -1));
    }

    [Fact]
    public void ADeckBoundDialThatIsExcludedEntersTheCycleAtAnEnd()
    {
        Assert.Equal("Odyssey G95NC", DisplayTargets.Neighbor(DisplayTargets.StreamDeck, Two, false, +1));
        Assert.Equal("SF10T", DisplayTargets.Neighbor(DisplayTargets.StreamDeck, Two, false, -1));
    }

    [Fact]
    public void NothingToCycleWithOneTarget()
    {
        Assert.Null(DisplayTargets.Neighbor("", One, false, +1));
        Assert.Null(DisplayTargets.Neighbor("", None, true, +1));
        Assert.Equal(DisplayTargets.StreamDeck, DisplayTargets.Neighbor("", One, true, +1));
    }

    [Fact]
    public void PositionsCountTheDeck()
    {
        Assert.Equal((1, 3), DisplayTargets.Position("Odyssey G95NC", Two, true));
        Assert.Equal((3, 3), DisplayTargets.Position(DisplayTargets.StreamDeck, Two, true));
        Assert.Equal((2, 2), DisplayTargets.Position("SF10T", Two, false));
        Assert.Equal((0, 0), DisplayTargets.Position(DisplayTargets.StreamDeck, Two, false));
        Assert.Equal((0, 0), DisplayTargets.Position(null, Two, true));
    }

    [Fact]
    public void TheDeckRendersLikeAMonitor()
    {
        var snapshot = DisplayBrightnessRenderer.FromStreamDeck(new BrightnessState(60, true), "Stream Deck +", 2, 3);
        Assert.Equal("Stream Deck +", snapshot.Name);
        Assert.True(snapshot.Dimmed);
        Assert.Equal("3/3", DisplayBrightnessRenderer.MonitorBadge(snapshot));
        Assert.Equal("Dimmed  60%", DisplayBrightnessRenderer.Render(snapshot, "tile").Track);
    }

    [Fact]
    public void ABindingSurvivesAPresentationOnlyRename()
    {
        // A dial bound before "StudioDisplay" was tidied to "Studio Display"
        // must keep working rather than reporting the screen as disconnected.
        string[] monitors = ["Studio Display XDR", "BenQ MA270S", "Studio Display"];
        Assert.Equal("Studio Display", DisplayTargets.Resolve("StudioDisplay", monitors, includeStreamDeck: false));
    }

    [Fact]
    public void TheLooseMatchDoesNotConflateDifferentMonitors()
    {
        string[] monitors = ["Studio Display XDR", "Studio Display"];
        Assert.Equal("Studio Display XDR", DisplayTargets.Resolve("StudioDisplayXDR", monitors, includeStreamDeck: false));
        Assert.Null(DisplayTargets.Resolve("Studio Display Pro", monitors, includeStreamDeck: false));
        Assert.Null(DisplayTargets.Resolve("BenQ MA270S", monitors, includeStreamDeck: false));
    }
}
