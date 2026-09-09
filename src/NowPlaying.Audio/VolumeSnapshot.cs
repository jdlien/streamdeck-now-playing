namespace NowPlaying.Audio;

/// <summary>The default output device's volume state, as last reported.</summary>
/// <param name="DeviceName">The device's friendly name, e.g. "Speakers (Realtek(R) Audio)" or "Studio Display XDR Speakers".</param>
/// <param name="Level">Master volume as a scalar 0..1. Retained while muted. Meaningless when <paramref name="CanSetVolume"/> is false.</param>
/// <param name="Muted">Whether the endpoint is muted.</param>
/// <param name="HasDevice">False when there is no default render device at all.</param>
/// <param name="CanSetVolume">
/// Whether the device exposes a software volume. WASAPI always does, so this
/// is true on Windows. CoreAudio does not: interfaces with a physical gain
/// knob (measured on an SSL 2 MkII) expose no virtual main volume at all, and
/// macOS greys out its own slider for them (macos-port-plan D4).
/// </param>
/// <param name="CanMute">
/// Whether the device exposes mute. Independent of <paramref name="CanSetVolume"/>:
/// a device can have one without the other, so the dial must not offer a press
/// that cannot work.
/// </param>
public sealed record VolumeSnapshot(
    string DeviceName,
    float Level,
    bool Muted,
    bool HasDevice,
    bool CanSetVolume = true,
    bool CanMute = true)
{
    public static VolumeSnapshot NoDevice { get; } = new("No audio device", 0f, false, false, false, false);

    /// <summary>A device that plays audio but offers no software control of it.</summary>
    public static VolumeSnapshot HardwareOnly(string deviceName) => new(deviceName, 0f, false, true, false, false);

    /// <summary>Level as a whole percentage.</summary>
    public int Percent => (int)Math.Round(Math.Clamp(Level, 0f, 1f) * 100);
}
