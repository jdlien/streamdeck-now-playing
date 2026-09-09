namespace NowPlaying.Plugin;

/// <summary>What turning the media dial should do right now.</summary>
public enum DialTurn
{
    /// <summary>Do nothing. Reserved combinations land here.</summary>
    Ignore,

    /// <summary>Skip to the next or previous track, per the sign of the rotation.</summary>
    SkipTrack,

    /// <summary>Move the system volume.</summary>
    Volume,
}

/// <summary>
/// The media dial's gesture rules (README section 7). Pure, because the
/// interesting part is not the events but a piece of timing that is easy to get
/// subtly wrong: in volume mode the play/pause toggle has to move from press to
/// release, and be suppressed when the dial was turned while held, or a
/// hold-and-turn to skip would also toggle playback on the way in.
/// </summary>
public static class DialGestures
{
    /// <summary>Turning skips tracks. The original behaviour and the default.</summary>
    public const string TrackMode = "track";

    /// <summary>Turning changes volume; holding the dial in while turning skips tracks.</summary>
    public const string VolumeMode = "volume";

    /// <summary>What a rotation means, given the mode and whether the dial is held in.</summary>
    public static DialTurn OnRotate(string mode, bool pressed) => mode == VolumeMode
        ? (pressed ? DialTurn.SkipTrack : DialTurn.Volume)
        : (pressed ? DialTurn.Ignore : DialTurn.SkipTrack);

    /// <summary>
    /// Whether pressing down toggles immediately. Only in track mode: volume
    /// mode cannot decide until it knows whether this press is also a turn.
    /// </summary>
    public static bool TogglesOnPress(string mode) => mode != VolumeMode;

    /// <summary>
    /// Whether releasing toggles. Only in volume mode, and only when the dial
    /// was not turned while held, so a skip gesture does not also toggle.
    /// </summary>
    public static bool TogglesOnRelease(string mode, bool rotatedWhilePressed) =>
        mode == VolumeMode && !rotatedWhilePressed;
}
