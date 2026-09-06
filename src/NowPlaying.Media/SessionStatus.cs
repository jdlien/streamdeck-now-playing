namespace NowPlaying.Media;

/// <summary>
/// The six playback statuses Windows reports, plus one for a session whose
/// status could not be read. Kept free of WinRT types so the ranking logic
/// and its tests stay pure.
/// </summary>
public enum SessionStatus
{
    Unreadable,
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused,
}

public static class SessionStatusExtensions
{
    /// <summary>How a status is shown, once a session has been chosen.</summary>
    public static PlaybackState ToPlaybackState(this SessionStatus status) => status switch
    {
        SessionStatus.Playing => PlaybackState.Playing,
        SessionStatus.Paused => PlaybackState.Paused,
        SessionStatus.Stopped => PlaybackState.Stopped,
        SessionStatus.Changing => PlaybackState.Changing,
        _ => PlaybackState.None,
    };
}
