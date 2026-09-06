namespace NowPlaying.Media;

/// <summary>
/// An immutable view of the chosen media session (README section 5.2).
/// Published by the session service whenever something changes; the renderer
/// turns it plus the wall clock into what the strip shows.
/// </summary>
/// <param name="AppId">The session's SourceAppUserModelId, or null when there is no session.</param>
/// <param name="State">Collapsed playback state.</param>
/// <param name="Title">Normalised title, possibly empty.</param>
/// <param name="Artist">Normalised artist (Artist, else AlbumArtist, else AlbumTitle), possibly empty.</param>
/// <param name="Position">Position relative to the track start as last reported by the player.</param>
/// <param name="PositionAt">When the player reported that position (its LastUpdatedTime), or null if never.</param>
/// <param name="Duration">Track length, or null when unknown or not usable (live streams, foobar2000).</param>
/// <param name="CanNext">The player accepts a skip-next command right now.</param>
/// <param name="CanPrevious">The player accepts a skip-previous command right now.</param>
/// <param name="CanToggle">The player accepts a play/pause toggle right now.</param>
public sealed record NowPlayingSnapshot(
    string? AppId,
    PlaybackState State,
    string Title,
    string Artist,
    TimeSpan? Position,
    DateTimeOffset? PositionAt,
    TimeSpan? Duration,
    bool CanNext,
    bool CanPrevious,
    bool CanToggle)
{
    /// <summary>The "No media" snapshot.</summary>
    public static NowPlayingSnapshot Empty { get; } =
        new(null, PlaybackState.None, "", "", null, null, null, false, false, false);

    /// <summary>
    /// Ages of a reported position that are trusted for extrapolation
    /// (README section 5.4). Negative means the clocks disagree; ten minutes
    /// or more means the timestamp is meaningless.
    /// </summary>
    public static readonly TimeSpan MaxTrustedAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Where the track is at <paramref name="now"/>: the reported position,
    /// advanced by the age of the report only while playing and only when
    /// that age is plausible, clamped to the duration when one is known.
    /// Arithmetic stays on TimeSpan/DateTimeOffset so it is exact in ticks.
    /// </summary>
    public TimeSpan? EffectivePosition(DateTimeOffset now)
    {
        if (Position is not { } position)
        {
            return null;
        }

        if (State == PlaybackState.Playing && PositionAt is { } reportedAt)
        {
            var age = now - reportedAt;
            if (age >= TimeSpan.Zero && age < MaxTrustedAge)
            {
                position += age;
            }
        }

        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (Duration is { } duration && position > duration)
        {
            position = duration;
        }

        return position;
    }
}
