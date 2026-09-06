using NowPlaying.Media;
using SkiaSharp;

namespace NowPlaying.Plugin;

/// <summary>
/// Composes album art with a play/pause glyph and, on keys, the text
/// (README section 6). Legibility comes from fixed dark scrims under white
/// glyphs and text rather than from estimating the art's brightness, so
/// every cover gets the same treatment.
/// </summary>
public static class ArtRenderer
{
    private static readonly SKColor Background = new(0x1e, 0x1e, 0x22);
    private static readonly SKColor DimGlyph = new(0x70, 0x70, 0x78);
    private static readonly SKColor BadgeScrim = new(0, 0, 0, 150);
    private static readonly SKColor TileScrim = new(0, 0, 0, 120);
    private static readonly SKColor ArtistColor = new(0xd0, 0xd0, 0xd4);
    private static readonly SKSamplingOptions Sampling = new(SKCubicResampler.Mitchell);

    /// <summary>The layout's icon slot is 36 x 36; the tile is drawn 1:1 to avoid resampling on the strip.</summary>
    public const int DialTileSize = 36;

    /// <summary>Keys are drawn at the @2x size the app expects for crisp text.</summary>
    public const int KeySize = 144;

    private const float KeyPadding = 8f;
    private const float TitleFontSize = 20f;
    private const float ArtistFontSize = 16f;
    private const int MaxTitleLines = 2;

    /// <summary>
    /// A key image: the art full-bleed with a state badge in the top-right
    /// corner and, when text is given, the title (up to two lines) and the
    /// artist left-aligned over a dark gradient along the bottom. Without art
    /// the glyph sits large on the dark background instead of the badge.
    /// </summary>
    public static byte[] RenderKey(PlaybackState state, byte[]? art, string? title = null, string? artist = null, int size = KeySize)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(Background);

        title = state == PlaybackState.None ? "" : (title ?? "").Trim();
        artist = state == PlaybackState.None ? "" : (artist ?? "").Trim();
        var hasText = title.Length > 0 || artist.Length > 0;

        using var image = Decode(art);
        if (image is not null && state != PlaybackState.None)
        {
            DrawCover(canvas, image, new SKRect(0, 0, size, size), cornerRadius: 0);

            var radius = size * 0.19f;
            var margin = size * 0.05f;
            var cx = size - radius - margin;
            var cy = radius + margin;
            using var scrim = new SKPaint { Color = BadgeScrim, IsAntialias = true };
            canvas.DrawCircle(cx, cy, radius, scrim);
            DrawGlyph(canvas, state, cx, cy, radius * 1.1f, SKColors.White);
        }
        else
        {
            var color = state == PlaybackState.None ? DimGlyph : SKColors.White;
            var cy = hasText ? size * 0.36f : size / 2f;
            DrawGlyph(canvas, state, size / 2f, cy, size * 0.46f, color);
        }

        if (hasText)
        {
            DrawTextBand(canvas, size, title, artist);
        }

        return Encode(surface);
    }

    /// <summary>
    /// The dial's icon tile: the art under a light scrim with the glyph
    /// centred on it. A corner badge would be too small at this size, so the
    /// whole tile carries the state.
    /// </summary>
    public static byte[] RenderDialIcon(PlaybackState state, byte[]? art, int size = DialTileSize)
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

    /// <summary>
    /// The volume dial's tile, in the art tile's frame so the two dials match:
    /// a dark rounded square with a white speaker, waves when live and a
    /// cross when muted.
    /// </summary>
    public static byte[] RenderVolumeTile(bool muted, int size = DialTileSize)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var rect = new SKRect(0, 0, size, size);
        var corner = size * 0.14f;
        using var tile = new SKPaint { Color = new SKColor(0x2c, 0x2c, 0x32), IsAntialias = true };
        canvas.DrawRoundRect(rect, corner, corner, tile);

        DrawSpeaker(canvas, size / 2f, size / 2f, size * 0.62f, muted, muted ? DimGlyph : SKColors.White);
        return Encode(surface);
    }

    /// <summary>The brightness dial's tile: a sun with eight rays, greyed when the screen is dimmed.</summary>
    public static byte[] RenderBrightnessTile(bool dimmed, int size = DialTileSize)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var rect = new SKRect(0, 0, size, size);
        var corner = size * 0.14f;
        using var tile = new SKPaint { Color = new SKColor(0x2c, 0x2c, 0x32), IsAntialias = true };
        canvas.DrawRoundRect(rect, corner, corner, tile);

        var color = dimmed ? DimGlyph : SKColors.White;
        var cx = size / 2f;
        var cy = size / 2f;
        var half = size * 0.31f;
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(cx, cy, half * 0.42f, fill);

        using var stroke = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.5f, half * 0.16f),
            StrokeCap = SKStrokeCap.Round,
        };
        for (var i = 0; i < 8; i++)
        {
            var angle = i * MathF.PI / 4f;
            var inner = half * 0.66f;
            var outer = half * 1.0f;
            canvas.DrawLine(
                cx + MathF.Cos(angle) * inner, cy + MathF.Sin(angle) * inner,
                cx + MathF.Cos(angle) * outer, cy + MathF.Sin(angle) * outer,
                stroke);
        }

        return Encode(surface);
    }

    /// <summary>Speaker body and cone with two sound arcs, or a cross when muted, fitted into a box of side <paramref name="box"/>.</summary>
    private static void DrawSpeaker(SKCanvas canvas, float cx, float cy, float box, bool muted, SKColor color)
    {
        var half = box / 2f;
        var offset = -half * 0.18f; // shift left so the arcs fit
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

        // Body: a small rounded rectangle; cone: a trapezoid opening to the right.
        var bodyLeft = cx + offset - half * 0.62f;
        var bodyRight = cx + offset - half * 0.28f;
        canvas.DrawRoundRect(new SKRect(bodyLeft, cy - half * 0.28f, bodyRight, cy + half * 0.28f), half * 0.06f, half * 0.06f, fill);
        using var cone = new SKPath();
        cone.MoveTo(bodyRight - half * 0.02f, cy - half * 0.28f);
        cone.LineTo(cx + offset + half * 0.12f, cy - half * 0.66f);
        cone.LineTo(cx + offset + half * 0.12f, cy + half * 0.66f);
        cone.LineTo(bodyRight - half * 0.02f, cy + half * 0.28f);
        cone.Close();
        canvas.DrawPath(cone, fill);

        using var stroke = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1.5f, half * 0.14f),
            StrokeCap = SKStrokeCap.Round,
        };

        if (muted)
        {
            var x = cx + offset + half * 0.55f;
            var r = half * 0.22f;
            canvas.DrawLine(x - r, cy - r, x + r, cy + r, stroke);
            canvas.DrawLine(x - r, cy + r, x + r, cy - r, stroke);
            return;
        }

        var arcCentre = new SKPoint(cx + offset + half * 0.14f, cy);
        foreach (var radius in new[] { half * 0.40f, half * 0.70f })
        {
            var oval = new SKRect(arcCentre.X - radius, arcCentre.Y - radius, arcCentre.X + radius, arcCentre.Y + radius);
            canvas.DrawArc(oval, -42f, 84f, false, stroke);
        }
    }

    // -- text ---------------------------------------------------------------

    private static void DrawTextBand(SKCanvas canvas, int size, string title, string artist)
    {
        using var titleFont = MakeFont(TitleFontSize, SKFontStyleWeight.SemiBold, title);
        using var artistFont = MakeFont(ArtistFontSize, SKFontStyleWeight.Normal, artist);

        var maxWidth = size - 2 * KeyPadding;
        var titleLines = title.Length > 0 ? WrapLines(title, titleFont, maxWidth, MaxTitleLines) : [];
        var artistLine = artist.Length > 0 ? Ellipsize(artist, artistFont, maxWidth) : null;

        var titleLineHeight = LineHeight(titleFont);
        var artistLineHeight = LineHeight(artistFont);
        var textHeight = titleLines.Count * titleLineHeight + (artistLine is null ? 0 : artistLineHeight);
        var textTop = size - KeyPadding - textHeight;

        // Gradient from clear, well above the text, to near-black at the bottom edge.
        using var gradient = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, textTop - size * 0.22f),
                new SKPoint(0, size),
                [SKColors.Transparent, new SKColor(0, 0, 0, 170), new SKColor(0, 0, 0, 225)],
                [0f, 0.45f, 1f],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(new SKRect(0, textTop - size * 0.22f, size, size), gradient);

        using var titlePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var artistPaint = new SKPaint { Color = ArtistColor, IsAntialias = true };

        var y = textTop;
        foreach (var line in titleLines)
        {
            canvas.DrawText(line, KeyPadding, y - titleFont.Metrics.Ascent, SKTextAlign.Left, titleFont, titlePaint);
            y += titleLineHeight;
        }

        if (artistLine is not null)
        {
            canvas.DrawText(artistLine, KeyPadding, y - artistFont.Metrics.Ascent, SKTextAlign.Left, artistFont, artistPaint);
        }
    }

    /// <summary>
    /// Segoe UI at the requested weight, or a system face that has the glyphs
    /// when the text needs a script Segoe UI lacks.
    /// </summary>
    private static SKFont MakeFont(float size, SKFontStyleWeight weight, string sample)
    {
        var typeface = SKTypeface.FromFamilyName("Segoe UI", weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.Default;

        if (sample.Length > 0 && !typeface.ContainsGlyphs(sample))
        {
            foreach (var rune in sample.EnumerateRunes())
            {
                if (typeface.ContainsGlyphs(rune.ToString()))
                {
                    continue;
                }

                var fallback = SKFontManager.Default.MatchCharacter(null, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright, null, rune.Value);
                if (fallback is not null)
                {
                    typeface = fallback;
                }

                break;
            }
        }

        return new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
    }

    private static float LineHeight(SKFont font) => (font.Metrics.Descent - font.Metrics.Ascent) * 1.02f;

    /// <summary>
    /// Greedy word wrap to at most <paramref name="maxLines"/> lines, breaking
    /// inside words only when a single word is wider than the line. The last
    /// line is ellipsized when text remains.
    /// </summary>
    public static IReadOnlyList<string> WrapLines(string text, SKFont font, float maxWidth, int maxLines)
    {
        var lines = new List<string>(maxLines);
        var remaining = text.Trim();
        while (remaining.Length > 0 && lines.Count < maxLines)
        {
            if (font.MeasureText(remaining) <= maxWidth)
            {
                lines.Add(remaining);
                break;
            }

            if (lines.Count == maxLines - 1)
            {
                lines.Add(Ellipsize(remaining, font, maxWidth));
                break;
            }

            var cut = FindBreak(remaining, font, maxWidth);
            lines.Add(remaining[..cut].TrimEnd());
            remaining = remaining[cut..].TrimStart();
        }

        return lines;
    }

    /// <summary>The text, or the longest prefix that fits with an ellipsis appended.</summary>
    public static string Ellipsize(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "…";
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + ellipsis;
            if (font.MeasureText(candidate) <= maxWidth)
            {
                return candidate;
            }
        }

        return ellipsis;
    }

    /// <summary>Index to cut at: after the last space whose prefix fits, else the longest prefix of characters that fits.</summary>
    private static int FindBreak(string text, SKFont font, float maxWidth)
    {
        for (var i = text.Length - 1; i > 0; i--)
        {
            if (text[i] == ' ' && font.MeasureText(text[..i]) <= maxWidth)
            {
                return i;
            }
        }

        for (var length = text.Length - 1; length > 1; length--)
        {
            if (font.MeasureText(text[..length]) <= maxWidth)
            {
                return length;
            }
        }

        return 1;
    }

    // -- drawing helpers ----------------------------------------------------

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
