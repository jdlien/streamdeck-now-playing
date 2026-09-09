using BarRaider.SdTools;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>The single display-brightness service shared by every instance of the action; each instance binds to its own monitor.</summary>
internal static class DisplayBrightnessHub
{
    private static readonly object Gate = new();
    private static IDisplayBrightnessService? _service;

    /// <summary>One monitor's snapshot changed.</summary>
    public static event Action<string, DisplayBrightnessSnapshot>? Changed;

    /// <summary>The set of monitors changed; bindings should be re-resolved.</summary>
    public static event Action? MonitorsChanged;

    public static void Attach()
    {
        lock (Gate)
        {
            if (_service is not null)
            {
                return;
            }

            var service = PlatformServices.CreateDisplayBrightness(message => Logger.Instance.LogMessage(TracingLevel.INFO, $"[display] {message}"));
            if (service is null)
            {
                Logger.Instance.LogMessage(TracingLevel.INFO, "[display] no implementation on this platform");
                return;
            }

            service.Changed += (name, snapshot) => Changed?.Invoke(name, snapshot);
            service.MonitorsChanged += () => MonitorsChanged?.Invoke();
            _service = service;
            service.Start();
        }
    }

    public static IReadOnlyList<string> MonitorNames => _service?.MonitorNames ?? Array.Empty<string>();

    public static string? Resolve(string? name) => _service?.Resolve(name);

    public static DisplayBrightnessSnapshot Get(string? name) => _service?.Get(name) ?? DisplayBrightnessSnapshot.Unavailable;

    public static string? Neighbor(string? name, int direction) => _service?.Neighbor(name, direction);

    public static bool Adjust(string? name, int delta) => _service?.Adjust(name, delta) ?? false;

    public static bool Toggle(string? name) => _service?.Toggle(name) ?? false;

    public static void Refresh() => _service?.Refresh();
}
