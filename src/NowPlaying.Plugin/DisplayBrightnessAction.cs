using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The display brightness dial, in the shared layout: one monitor's
/// brightness over DDC/CI. Each instance binds to a monitor of its own
/// (automatic = the primary), so two dials can serve two monitors. Turn to
/// adjust; press, tap, and hold each run a configurable gesture: dim and
/// restore, next monitor, previous monitor, or nothing.
/// </summary>
[PluginActionId("com.jdlien.now-playing.display-brightness")]
public sealed class DisplayBrightnessAction : EncoderBase
{
    private const string LayoutPath = "layouts/display-brightness.json";
    private const string MonitorKey = "monitor";
    private const string StepKey = "step";
    private const string PressKey = "pressAction";
    private const string TapKey = "tapAction";
    private const string HoldKey = "holdAction";
    private const string GestureDim = "dim";
    private const string GestureNext = "next";
    private const string GesturePrevious = "previous";
    private const string GestureNone = "none";
    private static readonly string[] Steps = ["1", "2", "5", "10"];
    private static readonly string[] Gestures = [GestureDim, GestureNext, GesturePrevious, GestureNone];

    private readonly object _gate = new();
    private readonly Action<string, DisplayBrightnessSnapshot> _onChanged;
    private readonly Action _onMonitorsChanged;
    private readonly Dictionary<bool, string> _tileCache = new();
    private JObject _settings = new();
    private FeedbackFrame? _lastFrame;
    private string? _lastBadge;
    private string _monitor = ""; // "" = automatic (primary)
    private int _stepPercent = 2;
    private string _press = GestureDim;
    private string _tap = GestureNext;
    private string _hold = GesturePrevious;

    public DisplayBrightnessAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] display brightness appear: context {connection.ContextId}");

        ApplySettings(payload.Settings, writeBackDefaults: true);
        PropertyInspectorBridge.Attach(connection);

        _onChanged = (name, snapshot) =>
        {
            if (string.Equals(name, DisplayBrightnessHub.Resolve(Monitor), StringComparison.OrdinalIgnoreCase))
            {
                Push(snapshot, full: false);
            }
        };
        _onMonitorsChanged = () => Push(Current, full: false);
        DisplayBrightnessHub.Changed += _onChanged;
        DisplayBrightnessHub.MonitorsChanged += _onMonitorsChanged;
        DisplayBrightnessHub.Attach();

        connection.OnSystemDidWakeUp += (_, _) => DisplayBrightnessHub.Refresh();
        connection.OnDeviceDidConnect += (_, _) => Push(Current, full: true);

        _ = ApplyLayoutAndPushAsync();
    }

    private string Monitor
    {
        get
        {
            lock (_gate)
            {
                return _monitor;
            }
        }
    }

    private DisplayBrightnessSnapshot Current => DisplayBrightnessHub.Get(Monitor);

    private async Task ApplyLayoutAndPushAsync()
    {
        try
        {
            await Connection.SetFeedbackLayoutAsync(LayoutPath);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] display setFeedbackLayout failed: {ex.Message}");
        }

        Push(Current, full: true);
    }

    // -- input --------------------------------------------------------------

    public override void DialRotate(DialRotatePayload payload)
    {
        if (payload.Ticks == 0 || payload.IsDialPressed)
        {
            return;
        }

        int step;
        lock (_gate)
        {
            step = _stepPercent;
        }

        if (!DisplayBrightnessHub.Adjust(Monitor, payload.Ticks * step))
        {
            _ = Connection.ShowAlert();
        }
    }

    public override void DialDown(DialPayload payload) => Perform(Gesture(PressKey));

    public override void DialUp(DialPayload payload)
    {
    }

    public override void TouchPress(TouchpadPressPayload payload) => Perform(Gesture(payload.IsLongPress ? HoldKey : TapKey));

    private string Gesture(string key)
    {
        lock (_gate)
        {
            return key switch
            {
                PressKey => _press,
                TapKey => _tap,
                _ => _hold,
            };
        }
    }

    private void Perform(string gesture)
    {
        switch (gesture)
        {
            case GestureDim:
                if (!DisplayBrightnessHub.Toggle(Monitor))
                {
                    _ = Connection.ShowAlert();
                }

                break;

            case GestureNext:
            case GesturePrevious:
                var target = DisplayBrightnessHub.Neighbor(Monitor, gesture == GestureNext ? 1 : -1);
                if (target is null)
                {
                    _ = Connection.ShowAlert(); // one monitor, or none: nothing to move to
                    return;
                }

                BindTo(target);
                break;

            default:
                break;
        }
    }

    /// <summary>Point this dial at a monitor by name and remember it in the action's settings.</summary>
    private void BindTo(string monitorName)
    {
        JObject settings;
        lock (_gate)
        {
            _monitor = monitorName;
            _settings[MonitorKey] = monitorName;
            settings = (JObject)_settings.DeepClone();
        }

        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] display brightness bound to {monitorName}");
        _ = Connection.SetSettingsAsync(settings);
        Push(Current, full: false);
    }

    // -- display --------------------------------------------------------------

    public override void OnTick()
    {
    }

    private void Push(DisplayBrightnessSnapshot snapshot, bool full)
    {
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var frame = DisplayBrightnessRenderer.Render(snapshot, TileFor(snapshot.Dimmed || !snapshot.Available));
            payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;

            // The badge is this layout's own item, outside the shared frame.
            var badge = DisplayBrightnessRenderer.MonitorBadge(snapshot);
            if (full || badge != _lastBadge)
            {
                payload[DisplayBrightnessRenderer.BadgeKey] = badge;
                _lastBadge = badge;
            }
        }

        if (payload.Count == 0)
        {
            return;
        }

        _ = SendAsync(payload);
    }

    private string TileFor(bool dimmed)
    {
        if (!_tileCache.TryGetValue(dimmed, out var dataUri))
        {
            dataUri = ArtRenderer.ToDataUri(ArtRenderer.RenderMonitorTile(dimmed));
            _tileCache[dimmed] = dataUri;
        }

        return dataUri;
    }

    private async Task SendAsync(Dictionary<string, object> payload)
    {
        try
        {
            await Connection.SetFeedbackAsync(FeedbackJson.ToJObject(payload));
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] display setFeedback failed: {ex.Message}");
        }
    }

    // -- settings and lifetime ------------------------------------------------

    public override void ReceivedSettings(ReceivedSettingsPayload payload)
    {
        ApplySettings(payload.Settings, writeBackDefaults: false);
        Push(Current, full: false);
    }

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) => GlobalSettingsStore.Apply(payload.Settings);

    private void ApplySettings(JObject? settings, bool writeBackDefaults)
    {
        lock (_gate)
        {
            _settings = settings is null ? new JObject() : (JObject)settings.DeepClone();
            var monitor = SettingsReader.GetString(settings, MonitorKey, PropertyInspectorBridge.AutomaticValue);
            _monitor = monitor == PropertyInspectorBridge.AutomaticValue ? "" : monitor;
            _stepPercent = int.Parse(SettingsReader.GetString(settings, StepKey, "2", Steps));
            _press = SettingsReader.GetString(settings, PressKey, GestureDim, Gestures);
            _tap = SettingsReader.GetString(settings, TapKey, GestureNext, Gestures);
            _hold = SettingsReader.GetString(settings, HoldKey, GesturePrevious, Gestures);
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, MonitorKey, StepKey, PressKey, TapKey, HoldKey))
        {
            JObject filled;
            lock (_gate)
            {
                _settings[MonitorKey] = _monitor.Length == 0 ? PropertyInspectorBridge.AutomaticValue : _monitor;
                _settings[StepKey] = _stepPercent.ToString();
                _settings[PressKey] = _press;
                _settings[TapKey] = _tap;
                _settings[HoldKey] = _hold;
                filled = (JObject)_settings.DeepClone();
            }

            _ = Connection.SetSettingsAsync(filled);
        }
    }

    public override void Dispose()
    {
        DisplayBrightnessHub.Changed -= _onChanged;
        DisplayBrightnessHub.MonitorsChanged -= _onMonitorsChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] display brightness disappear");
    }
}
