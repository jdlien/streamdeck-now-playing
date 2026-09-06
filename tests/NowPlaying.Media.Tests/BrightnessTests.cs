using NowPlaying.Device;
using NowPlaying.Plugin;
using SkiaSharp;

namespace NowPlaying.Media.Tests;

public class BrightnessTests
{
    [Fact]
    public void AdjustClampsAndTurnsBackOn()
    {
        var s = new BrightnessState(60, false);
        Assert.Equal(new BrightnessState(65, false), s.Adjust(5));
        Assert.Equal(new BrightnessState(100, false), s.Adjust(50));
        Assert.Equal(new BrightnessState(0, false), s.Adjust(-80));
        Assert.Equal(new BrightnessState(55, false), new BrightnessState(60, true).Adjust(-5));
    }

    [Fact]
    public void ToggleRemembersTheLevel()
    {
        var on = new BrightnessState(60, false);
        var off = on.Toggle();
        Assert.Equal(new BrightnessState(60, true), off);
        Assert.Equal(0, off.Effective);
        Assert.Equal(on, off.Toggle());
    }

    [Fact]
    public void TogglingOnFromZeroUsesTheFloor()
    {
        Assert.Equal(new BrightnessState(BrightnessState.RestoreFloor, false), new BrightnessState(0, true).Toggle());
    }

    [Fact]
    public void SavedLevelsAreClampedAndMissingMeansDefault()
    {
        Assert.Equal(BrightnessState.Default, BrightnessState.FromLevel(null));
        Assert.Equal(new BrightnessState(30, false), BrightnessState.FromLevel(30));
        Assert.Equal(new BrightnessState(100, false), BrightnessState.FromLevel(250));
    }

    [Fact]
    public void RendererFillsTheSharedLayout()
    {
        var frame = BrightnessRenderer.Render(new BrightnessState(60, false), "Stream Deck +", "tile");
        Assert.Equal(new FeedbackFrame("tile", "Brightness  60%", "Stream Deck +", true, 600, "0", "100"), frame);

        var off = BrightnessRenderer.Render(new BrightnessState(60, true), "Stream Deck +", "tile");
        Assert.Equal("Off  60%", off.Track);
        Assert.Equal(0, off.BarValue);
    }

    [Theory]
    [InlineData("StreamDeckPlus", "Stream Deck +")]
    [InlineData("StreamDeckXL", "Stream Deck XL")]
    [InlineData(null, "Stream Deck")]
    [InlineData("StreamDeckSomethingNew", "Stream Deck SomethingNew")]
    public void DeviceNamesAreReadable(string? type, string expected)
    {
        Assert.Equal(expected, BrightnessRenderer.DeviceName(type));
    }

    [Fact]
    public void TheSunTilesRenderAndDiffer()
    {
        var on = ArtRenderer.RenderBrightnessTile(off: false);
        var off = ArtRenderer.RenderBrightnessTile(off: true);
        Assert.NotEqual(on, off);
        using var bitmap = SKBitmap.Decode(on);
        Assert.Equal((ArtRenderer.DialTileSize, ArtRenderer.DialTileSize), (bitmap!.Width, bitmap.Height));
        var centre = bitmap.GetPixel(ArtRenderer.DialTileSize / 2, ArtRenderer.DialTileSize / 2);
        Assert.True(centre.Red > 240 && centre.Green > 240, "the sun's disc is white");
    }

    [Fact]
    public void DeviceEnumerationDoesNotThrow()
    {
        // Reads the PnP tree only; count depends on what is plugged in.
        var paths = StreamDeckHid.FindDevicePaths();
        Assert.All(paths, p => Assert.Contains("vid_0fd9&pid_0084", p, StringComparison.OrdinalIgnoreCase));
    }
}
