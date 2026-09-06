using NowPlaying.Device;
using NowPlaying.Plugin;
using SkiaSharp;

namespace NowPlaying.Media.Tests;

public class DisplayBrightnessTests
{
    /// <summary>The Odyssey G95NC reports a 0..50 range; the strip shows percent.</summary>
    [Theory]
    [InlineData(0u, 31u, 50u, 62)]
    [InlineData(0u, 0u, 50u, 0)]
    [InlineData(0u, 50u, 50u, 100)]
    [InlineData(0u, 75u, 50u, 100)]
    [InlineData(10u, 55u, 100u, 50)]
    [InlineData(5u, 5u, 5u, 0)]
    public void PercentFollowsTheMonitorsOwnRange(uint min, uint current, uint max, int expected)
    {
        Assert.Equal(expected, MonitorConfiguration.ToPercent(min, current, max));
    }

    [Theory]
    [InlineData(0u, 50u, 62, 31u)]
    [InlineData(0u, 50u, 0, 0u)]
    [InlineData(0u, 50u, 100, 50u)]
    [InlineData(0u, 50u, 150, 50u)]
    [InlineData(10u, 100u, 50, 55u)]
    [InlineData(5u, 5u, 40, 5u)]
    public void UnitsRoundTripFromPercent(uint min, uint max, int percent, uint expected)
    {
        Assert.Equal(expected, MonitorConfiguration.ToUnits(min, max, percent));
    }

    [Fact]
    public void SnapshotEffectiveIsZeroWhenDimmed()
    {
        var s = new DisplayBrightnessSnapshot("Odyssey G95NC", 62, false, true);
        Assert.Equal(62, s.Effective);
        Assert.Equal(0, (s with { Dimmed = true }).Effective);
    }

    [Fact]
    public void RendererFillsTheSharedLayoutAndHandlesUnavailable()
    {
        var frame = DisplayBrightnessRenderer.Render(new DisplayBrightnessSnapshot("Odyssey G95NC", 62, false, true), "tile");
        Assert.Equal(new FeedbackFrame("tile", "Brightness  62%", "Odyssey G95NC", true, 620, "0", "100"), frame);

        var dimmed = DisplayBrightnessRenderer.Render(new DisplayBrightnessSnapshot("Odyssey G95NC", 62, true, true), "tile");
        Assert.Equal("Dimmed  62%", dimmed.Track);
        Assert.Equal(0, dimmed.BarValue);

        var none = DisplayBrightnessRenderer.Render(DisplayBrightnessSnapshot.Unavailable, "tile");
        Assert.Equal("No DDC/CI monitor", none.Track);
        Assert.False(none.BarEnabled);
    }

    [Fact]
    public void TheMonitorLabelShowsThePositionOnlyWhenThereIsAChoice()
    {
        Assert.Equal("Odyssey G95NC", DisplayBrightnessRenderer.MonitorLabel(new DisplayBrightnessSnapshot("Odyssey G95NC", 62, false, true, 0, 1)));
        Assert.Equal("Odyssey G95NC  1/2", DisplayBrightnessRenderer.MonitorLabel(new DisplayBrightnessSnapshot("Odyssey G95NC", 62, false, true, 0, 2)));
        Assert.Equal("DELL U2723QE  2/2", DisplayBrightnessRenderer.MonitorLabel(new DisplayBrightnessSnapshot("DELL U2723QE", 40, false, true, 1, 2)));
    }

    [Fact]
    public void NextMonitorIsRefusedWithOneMonitorOrBeforeBinding()
    {
        using var service = new DisplayBrightnessService();
        Assert.False(service.NextMonitor(), "nothing is bound yet");
    }

    [Fact]
    public void TheMonitorTilesRenderAndDiffer()
    {
        var on = ArtRenderer.RenderMonitorTile(dimmed: false);
        var dimmed = ArtRenderer.RenderMonitorTile(dimmed: true);
        Assert.NotEqual(on, dimmed);
        using var bitmap = SKBitmap.Decode(on);
        Assert.Equal((ArtRenderer.DialTileSize, ArtRenderer.DialTileSize), (bitmap!.Width, bitmap.Height));
    }

    [Fact]
    public void MonitorEnumerationDoesNotThrowAndNamesAreNonEmpty()
    {
        var monitors = MonitorConfiguration.Enumerate();
        try
        {
            Assert.All(monitors, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
        }
        finally
        {
            MonitorConfiguration.Destroy(monitors);
        }
    }
}
