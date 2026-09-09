using NowPlaying.Audio;
using NowPlaying.Device;
using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>
/// Which implementation each hub gets, on Windows. Compiled only into the
/// Windows target; <c>PlatformServices.Unsupported.cs</c> is its counterpart
/// (macos-port-plan D2). Two files rather than #if inside one, so neither
/// platform's P/Invoke ever has to parse on the other.
/// </summary>
internal static class PlatformServices
{
    /// <summary>Whether this platform can drive the Stream Deck's own screen.</summary>
    public static bool SupportsStreamDeckBrightness => true;

    public static IMediaSessionService? CreateMedia(MediaSessionServiceOptions options) => new MediaSessionService(options);

    public static IVolumeService? CreateVolume(Action<string>? log) => new VolumeService(log);

    public static IStreamDeckBrightness? CreateStreamDeckBrightness(BrightnessState initial, Action<string>? log) =>
        new BrightnessService(initial, log);

    public static IDisplayBrightnessService? CreateDisplayBrightness(Action<string>? log) => new DisplayBrightnessService(log);
}
