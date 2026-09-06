using NowPlaying.Device;
using NowPlaying.Plugin;
using SkiaSharp;

namespace NowPlaying.Media.Tests;

public class BrightnessTests
{
    [Fact]
    public void AdjustClampsAndUndims()
    {
        var s = new BrightnessState(60, false);
        Assert.Equal(new BrightnessState(62, false), s.Adjust(2));
        Assert.Equal(new BrightnessState(100, false), s.Adjust(50));
        Assert.Equal(new BrightnessState(0, false), s.Adjust(-80));
        Assert.Equal(new BrightnessState(58, false), new BrightnessState(60, true).Adjust(-2));
    }

    [Fact]
    public void DimmingKeepsTheLevelAndSendsAGlow()
    {
        var on = new BrightnessState(60, false);
        var dimmed = on.Toggle();
        Assert.Equal(new BrightnessState(60, true), dimmed);
        Assert.Equal(BrightnessState.DimLevel, dimmed.Effective);
        Assert.Equal(4, dimmed.Effective);
        Assert.Equal(on, dimmed.Toggle());
    }

    [Fact]
    public void UndimmingFromNoBrighterThanTheGlowUsesTheFloor()
    {
        Assert.Equal(new BrightnessState(BrightnessState.RestoreFloor, false), new BrightnessState(0, true).Toggle());
        Assert.Equal(new BrightnessState(BrightnessState.RestoreFloor, false), new BrightnessState(4, true).Toggle());
        Assert.Equal(new BrightnessState(6, false), new BrightnessState(6, true).Toggle());
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

        var dimmed = BrightnessRenderer.Render(new BrightnessState(60, true), "Stream Deck +", "tile");
        Assert.Equal("Dimmed  60%", dimmed.Track);
        Assert.Equal(40, dimmed.BarValue);
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
        var on = ArtRenderer.RenderBrightnessTile(dimmed: false);
        var dimmed = ArtRenderer.RenderBrightnessTile(dimmed: true);
        Assert.NotEqual(on, dimmed);
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
