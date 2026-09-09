namespace NowPlaying.Media;

/// <summary>
/// Whether one snapshot differs from the next by enough to publish
/// (README section 6.3). Pure, and deliberately outside the platform services:
/// every implementation of <see cref="IMediaSessionService"/> owes callers the
/// same update policy, and the tests exercise it without a media stack.
/// </summary>
public static class SnapshotChange
{
    /// <summary>A position change beyond this, relative to the extrapolated position, is a seek and is published immediately.</summary>
    public static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(2);

    /// <summary>A change is significant unless only the reported position moved by less than <see cref="SeekThreshold"/>.</summary>
    public static bool IsSignificant(NowPlayingSnapshot previous, NowPlayingSnapshot next, DateTimeOffset now)
    {
        var previousAligned = previous with { Position = next.Position, PositionAt = next.PositionAt };
        if (previousAligned != next)
        {
            return true; // state, text, duration, or a control flag changed
        }

        var before = previous.EffectivePosition(now);
        var after = next.EffectivePosition(now);
        if (before is null || after is null)
        {
            return before != after;
        }

        var jump = after.Value - before.Value;
        return jump < -SeekThreshold || jump > SeekThreshold;
    }
}
