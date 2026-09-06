namespace NowPlaying.Audio;

/// <summary>The default output device's volume state, as last reported.</summary>
/// <param name="DeviceName">Windows' friendly name, e.g. "Speakers (Realtek(R) Audio)".</param>
/// <param name="Level">Master volume as a scalar 0..1. Retained while muted.</param>
/// <param name="Muted">Whether the endpoint is muted.</param>
/// <param name="HasDevice">False when there is no default render device at all.</param>
public sealed record VolumeSnapshot(string DeviceName, float Level, bool Muted, bool HasDevice)
{
    public static VolumeSnapshot NoDevice { get; } = new("No audio device", 0f, false, false);

    /// <summary>Level as a whole percentage.</summary>
    public int Percent => (int)Math.Round(Math.Clamp(Level, 0f, 1f) * 100);
}
