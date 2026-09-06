using BarRaider.SdTools;
using NowPlaying.Audio;

namespace NowPlaying.Plugin;

/// <summary>The single volume service shared by every volume action instance.</summary>
internal static class VolumeHub
{
    private static readonly object Gate = new();
    private static VolumeService? _service;

    /// <summary>Raised on a Core Audio callback thread whenever the volume state changes.</summary>
    public static event Action<VolumeSnapshot>? Changed;

    public static VolumeSnapshot Current => _service?.Current ?? VolumeSnapshot.NoDevice;

    public static void Attach()
    {
        lock (Gate)
        {
            if (_service is not null)
            {
                return;
            }

            var service = new VolumeService(message => Logger.Instance.LogMessage(TracingLevel.INFO, $"[volume] {message}"));
            service.Changed += snapshot => Changed?.Invoke(snapshot);
            _service = service;
            try
            {
                service.Start();
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.ERROR, $"[volume] start failed: {ex.Message}");
            }
        }
    }

    public static bool AdjustBy(float delta) => _service?.AdjustBy(delta) ?? false;

    public static bool ToggleMute() => _service?.ToggleMute() ?? false;
}
