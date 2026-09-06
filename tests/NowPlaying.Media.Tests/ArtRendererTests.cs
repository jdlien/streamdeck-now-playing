using NowPlaying.Media;
using NowPlaying.Plugin;
using SkiaSharp;

namespace NowPlaying.Media.Tests;

public class ArtRendererTests
{
    /// <summary>A 30 x 20 orange "cover", so the centre crop and scaling have something to do.</summary>
    private static byte[] SampleArt()
    {
        using var surface = SKSurface.Create(new SKImageInfo(30, 20));
        surface.Canvas.Clear(new SKColor(0xff, 0x88, 0x00));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png) ?? throw new InvalidOperationException("not a decodable image");

    [Theory]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Stopped)]
    [InlineData(PlaybackState.None)]
    public void TheKeyImageIsAFullSizePngWithOrWithoutArt(PlaybackState state)
    {
        using var withoutArt = Decode(ArtRenderer.RenderKey(state, null));
        Assert.Equal((144, 144), (withoutArt.Width, withoutArt.Height));

        using var withArt = Decode(ArtRenderer.RenderKey(state, SampleArt()));
        Assert.Equal((144, 144), (withArt.Width, withArt.Height));
    }

    [Fact]
    public void ArtFillsTheKeyAndTheBadgeSitsInTheCorner()
    {
        using var key = Decode(ArtRenderer.RenderKey(PlaybackState.Playing, SampleArt()));

        // Top-left is untouched art.
        var corner = key.GetPixel(8, 8);
        Assert.Equal((0xff, 0x88, 0x00), (corner.Red, corner.Green, corner.Blue));

        // The badge centre is darker than the art (scrim) but the glyph there is white-ish.
        var badgeCentre = key.GetPixel(144 - 23 - 9, 144 - 23 - 9);
        Assert.True(badgeCentre.Red > 200 && badgeCentre.Green > 200, "the glyph is white on the scrim");
    }

    [Fact]
    public void NoMediaShowsADimGlyphAndNoArtEvenWhenArtIsSupplied()
    {
        using var key = Decode(ArtRenderer.RenderKey(PlaybackState.None, SampleArt()));
        var corner = key.GetPixel(8, 8);
        Assert.Equal((0x1e, 0x1e, 0x22), (corner.Red, corner.Green, corner.Blue));
    }

    [Fact]
    public void PlayingAndPausedKeysDiffer()
    {
        Assert.NotEqual(ArtRenderer.RenderKey(PlaybackState.Playing, null), ArtRenderer.RenderKey(PlaybackState.Paused, null));
    }

    [Fact]
    public void TheDialTileIsTransparentOutsideItsRoundedCorners()
    {
        using var tile = Decode(ArtRenderer.RenderDialIcon(PlaybackState.Paused, SampleArt()));
        Assert.Equal((56, 56), (tile.Width, tile.Height));
        Assert.Equal(0, tile.GetPixel(0, 0).Alpha);
        Assert.True(tile.GetPixel(28, 4).Alpha > 200, "the tile itself is opaque");
    }

    [Fact]
    public void TheDialTileWithoutArtIsJustTheGlyph()
    {
        using var tile = Decode(ArtRenderer.RenderDialIcon(PlaybackState.Playing, null));
        Assert.Equal(0, tile.GetPixel(2, 2).Alpha);
        Assert.True(tile.GetPixel(24, 28).Alpha > 200, "the glyph is drawn");
    }

    [Fact]
    public void GarbageArtFallsBackToNoArt()
    {
        var withGarbage = ArtRenderer.RenderKey(PlaybackState.Playing, [1, 2, 3, 4]);
        Assert.Equal(ArtRenderer.RenderKey(PlaybackState.Playing, null), withGarbage);
    }

    [Fact]
    public void DataUrisAreWellFormed()
    {
        var uri = ArtRenderer.ToDataUri([0x89, 0x50, 0x4e, 0x47]);
        Assert.StartsWith("data:image/png;base64,", uri);
        Assert.Equal("iVBORw==", uri["data:image/png;base64,".Length..]);
    }
}
