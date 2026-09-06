namespace NowPlaying.Media;

/// <summary>
/// Fixes the position on resume. When a session goes from paused to playing,
/// its timeline still carries the report time from the pause, so extrapolating
/// by the age of that report would count the whole pause as playback (seen
/// live 2026-09-06: 3:29 became 4:15 of 4:15 for a moment). Until the player
/// reports a fresh timeline, the resume moment is the honest report time.
/// </summary>
internal sealed class ResumeCorrector
{
    private DateTimeOffset? _resumedAt;
    private DateTimeOffset? _staleReportAt;

    public NowPlayingSnapshot Apply(NowPlayingSnapshot previous, NowPlayingSnapshot raw, DateTimeOffset now)
    {
        if (raw.State != PlaybackState.Playing || raw.PositionAt is null)
        {
            Clear();
            return raw;
        }

        if (previous.State != PlaybackState.Playing && previous.AppId == raw.AppId)
        {
            // Just resumed: the report predates the resume, so its age is pause
            // time, not play time.
            _resumedAt = now;
            _staleReportAt = raw.PositionAt;
        }

        if (_resumedAt is { } resumedAt && raw.PositionAt == _staleReportAt)
        {
            return raw with { PositionAt = resumedAt };
        }

        Clear();
        return raw;
    }

    private void Clear()
    {
        _resumedAt = null;
        _staleReportAt = null;
    }
}
