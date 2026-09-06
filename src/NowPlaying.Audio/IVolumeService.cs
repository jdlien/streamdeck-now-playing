namespace NowPlaying.Audio;

/// <summary>
/// Volume of the default output device, kept current through Core Audio's
/// change notifications. One instance serves every action.
/// </summary>
public interface IVolumeService : IDisposable
{
    /// <summary>The latest known state. Never blocks on the audio stack.</summary>
    VolumeSnapshot Current { get; }

    /// <summary>Raised on a Core Audio callback thread whenever <see cref="Current"/> changes.</summary>
    event Action<VolumeSnapshot>? Changed;

    /// <summary>Bind to the default device and start listening. Later calls are no-ops.</summary>
    void Start();

    /// <summary>Move the level by <paramref name="delta"/> (scalar), clamped to 0..1, unmuting if muted. False when there is no device.</summary>
    bool AdjustBy(float delta);

    /// <summary>Set the level (scalar 0..1). False when there is no device.</summary>
    bool SetLevel(float level);

    /// <summary>Flip mute. False when there is no device.</summary>
    bool ToggleMute();
}
