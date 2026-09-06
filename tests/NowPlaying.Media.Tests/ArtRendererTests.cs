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

    private static bool IsOrange(SKColor c) => c.Red == 0xff && c.Green == 0x88 && c.Blue == 0x00;

    [Theory]
    [InlineData(PlaybackState.Playing)]
    [InlineData(PlaybackState.Paused)]
    [InlineData(PlaybackState.Stopped)]
    [InlineData(PlaybackState.None)]
    public void TheKeyImageIsAFullSizePngWithOrWithoutArtOrText(PlaybackState state)
    {
        using var plain = Decode(ArtRenderer.RenderKey(state, null));
        Assert.Equal((ArtRenderer.KeySize, ArtRenderer.KeySize), (plain.Width, plain.Height));

        using var full = Decode(ArtRenderer.RenderKey(state, SampleArt(), "Warriors of the Wasteland", "Michael Oakley"));
        Assert.Equal((ArtRenderer.KeySize, ArtRenderer.KeySize), (full.Width, full.Height));
    }

    [Fact]
    public void ArtFillsTheKeyAndTheBadgeSitsTopRight()
    {
        using var key = Decode(ArtRenderer.RenderKey(PlaybackState.Playing, SampleArt()));

        // Top-left and bottom-left are untouched art when there is no text.
        Assert.True(IsOrange(key.GetPixel(8, 8)));
        Assert.True(IsOrange(key.GetPixel(8, 136)));

        // The badge centre (top-right) carries the white glyph on the scrim.
        var radius = ArtRenderer.KeySize * 0.19f;
        var margin = ArtRenderer.KeySize * 0.05f;
        var badge = key.GetPixel((int)(ArtRenderer.KeySize - radius - margin) - 4, (int)(radius + margin));
        Assert.True(badge.Red > 200 && badge.Green > 200, "the glyph is white on the scrim");
    }

    [Fact]
    public void TextDarkensTheBottomBandAndLeavesTheTopAlone()
    {
        using var key = Decode(ArtRenderer.RenderKey(PlaybackState.Playing, SampleArt(), "Warriors of the Wasteland", "Michael Oakley"));
        Assert.True(IsOrange(key.GetPixel(8, 8)), "art above the band is untouched");

        var bottom = key.GetPixel(ArtRenderer.KeySize - 6, ArtRenderer.KeySize - 4);
        Assert.True(bottom.Red < 0x60, "the gradient darkens the bottom edge");

        // Somewhere along the title's first line there is white text.
        var sawWhite = false;
        for (var x = 10; x < 120 && !sawWhite; x++)
        {
            for (var y = 80; y < 140 && !sawWhite; y++)
            {
                var p = key.GetPixel(x, y);
                sawWhite = p.Red > 235 && p.Green > 235 && p.Blue > 235;
            }
        }

        Assert.True(sawWhite, "the title is drawn in white");
    }

    [Fact]
    public void NoMediaShowsADimGlyphAndNoArtOrText()
    {
        using var key = Decode(ArtRenderer.RenderKey(PlaybackState.None, SampleArt(), "leftover title", "leftover artist"));
        var corner = key.GetPixel(8, 8);
        Assert.Equal((0x1e, 0x1e, 0x22), (corner.Red, corner.Green, corner.Blue));
        var bottom = key.GetPixel(8, ArtRenderer.KeySize - 6);
        Assert.Equal((0x1e, 0x1e, 0x22), (bottom.Red, bottom.Green, bottom.Blue));
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
        Assert.Equal((ArtRenderer.DialTileSize, ArtRenderer.DialTileSize), (tile.Width, tile.Height));
        Assert.Equal(0, tile.GetPixel(0, 0).Alpha);
        Assert.True(tile.GetPixel(ArtRenderer.DialTileSize / 2, 3).Alpha > 200, "the tile itself is opaque");
    }

    [Fact]
    public void TheDialTileWithoutArtIsJustTheGlyph()
    {
        using var tile = Decode(ArtRenderer.RenderDialIcon(PlaybackState.Playing, null));
        Assert.Equal(0, tile.GetPixel(2, 2).Alpha);
        var centre = ArtRenderer.DialTileSize / 2;
        Assert.True(tile.GetPixel(centre - 2, centre).Alpha > 200, "the glyph is drawn");
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

    // -- wrapping -----------------------------------------------------------

    private static SKFont TestFont() => new(SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default, 21);

    [Fact]
    public void ShortTitlesStayOnOneLine()
    {
        using var font = TestFont();
        Assert.Equal(["Prologue"], ArtRenderer.WrapLines("Prologue", font, 126, 2));
    }

    [Fact]
    public void LongTitlesWrapAtSpacesAndKeepTheBeginning()
    {
        using var font = TestFont();
        var lines = ArtRenderer.WrapLines("Warriors of the Wasteland", font, 126, 2);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("Warriors of", lines[0]);
        Assert.True(font.MeasureText(lines[0]) <= 126);
        Assert.True(font.MeasureText(lines[1]) <= 126);
        // Either the whole title fits in two lines, or the second line is ellipsized; the beginning is never lost.
        var joined = string.Join(" ", lines);
        Assert.True(joined == "Warriors of the Wasteland" || lines[1].EndsWith("…"), joined);
        Assert.StartsWith("the", lines[1]);
    }

    [Fact]
    public void TextBeyondTheLastLineIsEllipsized()
    {
        using var font = TestFont();
        var lines = ArtRenderer.WrapLines("Remember (ESCM 12' Mix) Extended Club Version", font, 126, 2);
        Assert.Equal(2, lines.Count);
        Assert.EndsWith("…", lines[1]);
        Assert.True(font.MeasureText(lines[1]) <= 126);
    }

    [Fact]
    public void AWordWiderThanTheLineIsBrokenInsideTheWord()
    {
        using var font = TestFont();
        var lines = ArtRenderer.WrapLines("Supercalifragilisticexpialidocious", font, 126, 2);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Length > 3);
        Assert.True(font.MeasureText(lines[0]) <= 126);
    }

    [Fact]
    public void EllipsizeLeavesShortTextAlone()
    {
        using var font = TestFont();
        Assert.Equal("BT", ArtRenderer.Ellipsize("BT", font, 126));
        var cut = ArtRenderer.Ellipsize("Michael Oakley & Missing Words", font, 126);
        Assert.EndsWith("…", cut);
        Assert.True(font.MeasureText(cut) <= 126);
    }
}
