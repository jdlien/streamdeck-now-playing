using NowPlaying.Audio;
using NowPlaying.Plugin;
using SkiaSharp;

namespace NowPlaying.Media.Tests;

public class VolumeRendererTests
{
    [Fact]
    public void ALiveDeviceFillsEveryItem()
    {
        var frame = VolumeRenderer.Render(new VolumeSnapshot("Speakers (Realtek(R) Audio)", 0.42f, false, true), "tile");
        Assert.Equal(new FeedbackFrame("tile", "Volume  42%", "Speakers (Realtek(R) Audio)", true, 420, "0", "100"), frame);
    }

    [Fact]
    public void MutedKeepsTheLevelAndSaysSo()
    {
        var frame = VolumeRenderer.Render(new VolumeSnapshot("Headphones", 0.7f, true, true), "tile");
        Assert.Equal("Muted  70%", frame.Track);
        Assert.Equal(700, frame.BarValue);
        Assert.True(frame.BarEnabled);
    }

    [Fact]
    public void NoDeviceHidesTheBar()
    {
        var frame = VolumeRenderer.Render(VolumeSnapshot.NoDevice, "tile");
        Assert.Equal("No audio device", frame.Track);
        Assert.False(frame.BarEnabled);
        Assert.Equal("", frame.Elapsed);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.004f, 0)]
    [InlineData(0.006f, 1)]
    [InlineData(0.42f, 42)]
    [InlineData(1f, 100)]
    [InlineData(1.5f, 100)]
    public void PercentRoundsAndClamps(float level, int expected)
    {
        Assert.Equal(expected, new VolumeSnapshot("x", level, false, true).Percent);
    }

    [Fact]
    public void TheSpeakerTilesRenderAndDiffer()
    {
        var live = ArtRenderer.RenderVolumeTile(muted: false);
        var muted = ArtRenderer.RenderVolumeTile(muted: true);
        Assert.NotEqual(live, muted);

        using var bitmap = SKBitmap.Decode(live);
        Assert.Equal((ArtRenderer.DialTileSize, ArtRenderer.DialTileSize), (bitmap!.Width, bitmap.Height));
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.True(bitmap.GetPixel(ArtRenderer.DialTileSize / 2, 2).Alpha > 200, "the tile background is opaque");
    }
    [Fact]
    public void ADeviceWithNoSoftwareVolumeSaysSoAndHidesTheBar()
    {
        // Measured case: an SSL 2 MkII exposes no virtual main volume at all,
        // so there is no level to show and the dial cannot do anything.
        var frame = VolumeRenderer.Render(VolumeSnapshot.HardwareOnly("SSL 2 Mk II"), "tile");
        Assert.Equal(VolumeRenderer.HardwareOnlyText, frame.Track);
        Assert.Equal("SSL 2 Mk II", frame.Artist);
        Assert.False(frame.BarEnabled);
        Assert.Equal(0, frame.BarValue);
    }

    [Fact]
    public void HardwareOnlyIsDistinctFromNoDeviceAtAll()
    {
        var none = VolumeRenderer.Render(VolumeSnapshot.NoDevice, "tile");
        var fixedVolume = VolumeRenderer.Render(VolumeSnapshot.HardwareOnly("SSL 2 Mk II"), "tile");
        Assert.Equal(VolumeRenderer.NoDeviceText, none.Track);
        Assert.NotEqual(none.Track, fixedVolume.Track);
        Assert.Equal("", none.Artist);
    }

    [Fact]
    public void ADeviceThatCanSetVolumeButNotMuteStillRendersTheBar()
    {
        var frame = VolumeRenderer.Render(new VolumeSnapshot("Odd Device", 0.5f, false, true, CanSetVolume: true, CanMute: false), "tile");
        Assert.True(frame.BarEnabled);
        Assert.Equal("Volume  50%", frame.Track);
    }

}
