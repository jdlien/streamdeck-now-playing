using NowPlaying.Audio;
using NowPlaying.Device;
using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>
/// Which implementation each hub gets on every target that is not Windows.
/// Dispatch is at runtime rather than by target framework, so one neutral
/// build serves macOS and degrades on anything else: a null means "no
/// implementation here", and the hubs already treat that as "no device"
/// rather than throwing.
///
/// Still to land: media in M6. Stream Deck
/// brightness stays null permanently, because the Stream Deck app seizes the
/// HID device on macOS (macos-port-plan S2).
/// </summary>
internal static class PlatformServices
{
    /// <summary>
    /// False everywhere but Windows. The Stream Deck app opens the HID device
    /// with kIOHIDOptionsTypeSeizeDevice on macOS, so no other process can send
    /// it the brightness report (macos-port-plan S2). The action is hidden by a
    /// per-action OS key in the manifest; this keeps the deck out of the
    /// Display Brightness dial's cycle as well, so no dial can select a target
    /// that silently does nothing.
    /// </summary>
    public static bool SupportsStreamDeckBrightness => false;

    public static IMediaSessionService? CreateMedia(MediaSessionServiceOptions options) => null;

    public static IVolumeService? CreateVolume(Action<string>? log) =>
        OperatingSystem.IsMacOS() ? new MacVolumeService(log) : null;

    public static IStreamDeckBrightness? CreateStreamDeckBrightness(BrightnessState initial, Action<string>? log) => null;

    public static IDisplayBrightnessService? CreateDisplayBrightness(Action<string>? log) =>
        OperatingSystem.IsMacOS() ? new MacDisplayBrightnessService(log) : null;
}
