namespace NowPlaying.Media;

/// <summary>A session's timeline reduced to what the display needs.</summary>
/// <param name="Position">Position relative to the track start, or null when there is no usable duration.</param>
/// <param name="PositionAt">When the player reported that position, or null when it never has.</param>
/// <param name="Duration">Track length, or null when unknown or not usable.</param>
public readonly record struct NormalizedTimeline(TimeSpan? Position, DateTimeOffset? PositionAt, TimeSpan? Duration);

/// <summary>
/// Timeline arithmetic (README section 5.4). Everything stays on TimeSpan and
/// DateTimeOffset, whose ticks are the same 100 ns unit WinRT uses, so the
/// differences are exact. Converting to floating-point seconds first loses a
/// second on ordinary tracks.
/// </summary>
public static class TimelineMath
{
    /// <summary>
    /// WinRT's DateTime zero. A session that has never reported a timeline
    /// update leaves LastUpdatedTime here; foobar2000 does exactly this.
    /// </summary>
    public static readonly DateTimeOffset WinRtEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Durations at or beyond this are treated as garbage (live streams, broken players).</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    public static NormalizedTimeline Normalize(TimeSpan start, TimeSpan end, TimeSpan position, DateTimeOffset lastUpdated)
    {
        var duration = end - start;
        if (duration <= TimeSpan.Zero || duration >= MaxDuration)
        {
            return default;
        }

        var relative = position - start;
        if (relative < TimeSpan.Zero)
        {
            relative = TimeSpan.Zero;
        }
        else if (relative > duration)
        {
            relative = duration;
        }

        DateTimeOffset? reportedAt = lastUpdated <= WinRtEpoch ? null : lastUpdated;
        return new NormalizedTimeline(relative, reportedAt, duration);
    }
}
