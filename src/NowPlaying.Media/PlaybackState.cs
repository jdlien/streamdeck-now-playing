namespace NowPlaying.Media;

/// <summary>
/// The playback states the display distinguishes. Windows reports six
/// (Closed, Opened, Changing, Stopped, Playing, Paused); README section 5.3
/// says which of those are candidates for display and how the rest collapse.
/// </summary>
public enum PlaybackState
{
    /// <summary>No session worth showing.</summary>
    None,
    Playing,
    Paused,
    Stopped,
    /// <summary>Transient, between tracks. The previous display is kept.</summary>
    Changing,
}
