using BarRaider.SdTools;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>The single display-brightness service shared by every instance of the action.</summary>
internal static class DisplayBrightnessHub
{
    private static readonly object Gate = new();
    private static DisplayBrightnessService? _service;

    public static event Action<DisplayBrightnessSnapshot>? Changed;

    public static DisplayBrightnessSnapshot Current => _service?.Current ?? DisplayBrightnessSnapshot.Unavailable;

    public static void Attach()
    {
        lock (Gate)
        {
            if (_service is not null)
            {
                return;
            }

            var service = new DisplayBrightnessService(message => Logger.Instance.LogMessage(TracingLevel.INFO, $"[display] {message}"));
            service.Changed += snapshot => Changed?.Invoke(snapshot);
            _service = service;
            service.Start();
        }
    }

    public static bool Adjust(int delta) => _service?.Adjust(delta) ?? false;

    public static bool Toggle() => _service?.Toggle() ?? false;

    public static void Refresh() => _service?.Refresh();
}
