namespace NowPlaying.Media;

/// <summary>
/// Turns the raw metadata fields into the one artist line the display shows.
/// </summary>
public static class MetadataNormalizer
{
    /// <summary>The separator Apple Music uses to pack "Artist — Album" into the Artist field.</summary>
    public const string EmDashSeparator = " — ";

    /// <summary>
    /// Artist, else AlbumArtist, else AlbumTitle. When the player reports no
    /// album of its own and the artist contains " — ", the part after the
    /// dash is the album (measured with Apple Music on 2026-09-06: two tracks
    /// from the same album carried the same suffix) and is dropped. Players
    /// that supply a real album title are left alone.
    /// </summary>
    public static string Artist(string artist, string albumArtist, string albumTitle)
    {
        var line = artist.Length > 0 ? artist
            : albumArtist.Length > 0 ? albumArtist
            : albumTitle;

        if (albumTitle.Length > 0)
        {
            return line;
        }

        var dash = line.IndexOf(EmDashSeparator, StringComparison.Ordinal);
        if (dash <= 0)
        {
            return line;
        }

        var before = line[..dash].Trim();
        return before.Length > 0 ? before : line;
    }
}
