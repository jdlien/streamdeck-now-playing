using BarRaider.SdTools;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The single brightness service shared by every brightness action. The
/// level lives in global settings so it survives restarts and is the same
/// on every page; the "dimmed" toggle is per process.
/// </summary>
internal static class BrightnessHub
{
    private static readonly object Gate = new();
    private static IStreamDeckBrightness? _service;
    private static bool _loadedFromSettings;

    /// <summary>Whether this platform can drive the deck's screen at all.</summary>
    public static bool IsSupported => PlatformServices.SupportsStreamDeckBrightness;

    /// <summary>Raised after every applied change.</summary>
    public static event Action<BrightnessState>? Changed;

    public static BrightnessState Current => _service?.Current ?? BrightnessState.Default;

    public static void Attach()
    {
        lock (Gate)
        {
            if (_service is not null)
            {
                return;
            }

            var service = PlatformServices.CreateStreamDeckBrightness(
                BrightnessState.Default,
                message => Logger.Instance.LogMessage(TracingLevel.INFO, $"[brightness] {message}"));
            if (service is null)
            {
                Logger.Instance.LogMessage(TracingLevel.INFO, "[brightness] no implementation on this platform");
                return;
            }

            service.Changed += (state, applied) =>
            {
                Logger.Instance.LogMessage(TracingLevel.INFO, $"[brightness] {state.Effective}% ({(state.Dimmed ? "dimmed" : "on")}, level {state.Level}) -> {applied} device(s)");
                Changed?.Invoke(state);
            };
            _service = service;
        }
    }

    /// <summary>
    /// Take the saved level from global settings the first time it arrives.
    /// Later arrivals are our own writes echoed back and are ignored, so a
    /// dial turn in progress is not overwritten.
    /// </summary>
    public static void LoadSavedLevel(int? level)
    {
        IStreamDeckBrightness? service;
        lock (Gate)
        {
            if (_loadedFromSettings || _service is null)
            {
                return;
            }

            _loadedFromSettings = true;
            service = _service;
        }

        service.Set(BrightnessState.FromLevel(level));
    }

    public static BrightnessState Adjust(int delta)
    {
        _service?.Adjust(delta);
        return Current;
    }

    public static BrightnessState Toggle()
    {
        _service?.Toggle();
        return Current;
    }

    public static void Reapply() => _service?.Reapply();
}
