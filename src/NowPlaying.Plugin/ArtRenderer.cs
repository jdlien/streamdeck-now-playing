using NowPlaying.Media;
using SkiaSharp;

namespace NowPlaying.Plugin;

/// <summary>
/// Composes album art with a play/pause glyph (README section 6). Legibility
/// comes from a fixed dark scrim under a white glyph rather than from
/// estimating the art's brightness, so every cover gets the same treatment.
/// </summary>
public static class ArtRenderer
{
    private static readonly SKColor Background = new(0x1e, 0x1e, 0x22);
    private static readonly SKColor DimGlyph = new(0x70, 0x70, 0x78);
    private static readonly SKColor BadgeScrim = new(0, 0, 0, 150);
    private static readonly SKColor TileScrim = new(0, 0, 0, 120);
    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// A key image: the art full-bleed with a state badge in the bottom-right
    /// corner, or without art a large centred glyph on the dark background.
    /// </summary>
    public static byte[] RenderKey(PlaybackState state, byte[]? art, int size = 144)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(Background);

        using var image = Decode(art);
        if (image is not null && state != PlaybackState.None)
        {
            DrawCover(canvas, image, new SKRect(0, 0, size, size), cornerRadius: 0);

            var radius = size * 0.16f;
            var cx = size - radius - size * 0.06f;
            var cy = size - radius - size * 0.06f;
            using var scrim = new SKPaint { Color = BadgeScrim, IsAntialias = true };
            canvas.DrawCircle(cx, cy, radius, scrim);
            DrawGlyph(canvas, state, cx, cy, radius * 1.05f, SKColors.White);
        }
        else
        {
            var color = state == PlaybackState.None ? DimGlyph : SKColors.White;
            DrawGlyph(canvas, state, size / 2f, size / 2f, size * 0.46f, color);
        }

        return Encode(surface);
    }

    /// <summary>
    /// The dial's icon tile, drawn 1:1 at the layout's 56 px slot: the art
    /// under a light scrim with the glyph centred on it. A corner badge would
    /// be too small at this size, so the whole tile carries the state.
    /// </summary>
    public static byte[] RenderDialIcon(PlaybackState state, byte[]? art, int size = 56)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var rect = new SKRect(0, 0, size, size);
        var corner = size * 0.14f;
        using var image = Decode(art);
        if (image is not null)
        {
            DrawCover(canvas, image, rect, corner);
            using var scrim = new SKPaint { Color = TileScrim, IsAntialias = true };
            canvas.DrawRoundRect(rect, corner, corner, scrim);
        }

        DrawGlyph(canvas, state, size / 2f, size / 2f, size * 0.62f, SKColors.White);
        return Encode(surface);
    }

    private static SKImage? Decode(byte[]? art) => art is { Length: > 0 } ? SKImage.FromEncodedData(art) : null;

    /// <summary>Centre-crop the image to a square and draw it into <paramref name="dest"/>.</summary>
    private static void DrawCover(SKCanvas canvas, SKImage image, SKRect dest, float cornerRadius)
    {
        var side = Math.Min(image.Width, image.Height);
        var source = new SKRect(
            (image.Width - side) / 2f,
            (image.Height - side) / 2f,
            (image.Width + side) / 2f,
            (image.Height + side) / 2f);

        canvas.Save();
        if (cornerRadius > 0)
        {
            canvas.ClipRoundRect(new SKRoundRect(dest, cornerRadius), SKClipOperation.Intersect, antialias: true);
        }

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, source, dest, Sampling, paint);
        canvas.Restore();
    }

    /// <summary>Fit the state glyph into a box of side <paramref name="box"/> centred at (cx, cy).</summary>
    private static void DrawGlyph(SKCanvas canvas, PlaybackState state, float cx, float cy, float box, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        var half = box / 2f;

        if (state is PlaybackState.Playing or PlaybackState.Changing)
        {
            // Play triangle, nudged right so its visual centre sits on cx.
            using var path = new SKPath();
            var left = cx - half * 0.72f;
            var right = cx + half * 0.92f;
            path.MoveTo(left, cy - half * 0.9f);
            path.LineTo(right, cy);
            path.LineTo(left, cy + half * 0.9f);
            path.Close();
            canvas.DrawPath(path, paint);
            return;
        }

        // Pause bars for Paused, Stopped, and None.
        var barWidth = half * 0.55f;
        var gap = half * 0.36f;
        var top = cy - half * 0.9f;
        var bottom = cy + half * 0.9f;
        var radius = barWidth * 0.25f;
        canvas.DrawRoundRect(new SKRect(cx - gap / 2 - barWidth, top, cx - gap / 2, bottom), radius, radius, paint);
        canvas.DrawRoundRect(new SKRect(cx + gap / 2, top, cx + gap / 2 + barWidth, bottom), radius, radius, paint);
    }

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A PNG as a data URI, the form the layout's pixmap item accepts.</summary>
    public static string ToDataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);
}
