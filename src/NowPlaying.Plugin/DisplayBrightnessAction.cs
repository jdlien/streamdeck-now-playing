using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The display brightness dial, in the shared layout. Each instance binds
/// to a target of its own: a monitor over DDC/CI (automatic = the primary),
/// or the Stream Deck's own screen. Turn to adjust; press, tap, and hold
/// each run a configurable gesture: dim and restore, next target, previous
/// target, or nothing. The Stream Deck can be included in the cycle, which
/// makes this one dial cover every screen on the desk.
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
    private const string IncludeStreamDeckKey = "includeStreamDeck";
    private const string RenameKey = "renameTo";
    private const string GestureDim = "dim";
    private const string GestureNext = "next";
    private const string GesturePrevious = "previous";
    private const string GestureNone = "none";
    private static readonly string[] Steps = ["1", "2", "5", "10"];
    private static readonly string[] Gestures = [GestureDim, GestureNext, GesturePrevious, GestureNone];

    private readonly object _gate = new();
    private readonly Action<string, DisplayBrightnessSnapshot> _onMonitorChanged;
    private readonly Action _onMonitorsChanged;
    private readonly Action<BrightnessState> _onStreamDeckChanged;
    private readonly Dictionary<string, string> _tileCache = new();
    private readonly string _deviceName;
    private JObject _settings = new();
    private FeedbackFrame? _lastFrame;
    private string? _lastBadge;
    private string _binding = DisplayTargets.Automatic;
    private bool _includeStreamDeck = true;
    private int _stepPercent = 2;
    private string _press = GestureDim;
    private string _tap = GestureNext;
    private string _hold = GesturePrevious;

    public DisplayBrightnessAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] display brightness appear: context {connection.ContextId}");

        string? deviceType = null;
        try
        {
            deviceType = connection.DeviceInfo()?.Type.ToString();
        }
        catch
        {
        }

        _deviceName = BrightnessRenderer.DeviceName(deviceType);

        ApplySettings(payload.Settings, writeBackDefaults: true);
        PropertyInspectorBridge.Attach(connection);

        _onMonitorChanged = (name, _) =>
        {
            if (string.Equals(name, ResolvedTarget(), StringComparison.OrdinalIgnoreCase))
            {
                Push(Current, full: false);
            }
        };
        _onMonitorsChanged = () => Push(Current, full: false);
        _onStreamDeckChanged = _ =>
        {
            if (ResolvedTarget() == DisplayTargets.StreamDeck)
            {
                Push(Current, full: false);
            }
        };
        DisplayBrightnessHub.Changed += _onMonitorChanged;
        DisplayBrightnessHub.MonitorsChanged += _onMonitorsChanged;
        BrightnessHub.Changed += _onStreamDeckChanged;
        DisplayBrightnessHub.Attach();
        BrightnessHub.Attach();
        _ = connection.GetGlobalSettingsAsync();

        connection.OnSystemDidWakeUp += (_, _) =>
        {
            DisplayBrightnessHub.Refresh();
            BrightnessHub.Reapply();
        };
        connection.OnDeviceDidConnect += (_, _) =>
        {
            BrightnessHub.Reapply();
            Push(Current, full: true);
        };

        _ = ApplyLayoutAndPushAsync();
    }

    // -- the target -----------------------------------------------------------

    private (string Binding, bool IncludeStreamDeck) BindingSnapshot()
    {
        lock (_gate)
        {
            return (_binding, _includeStreamDeck);
        }
    }

    /// <summary>The concrete target this dial controls right now: a monitor name, the Stream Deck, or null.</summary>
    private string? ResolvedTarget()
    {
        var (binding, include) = BindingSnapshot();
        return DisplayTargets.Resolve(binding, DisplayBrightnessHub.MonitorNames, include);
    }

    private DisplayBrightnessSnapshot Current
    {
        get
        {
            var (binding, include) = BindingSnapshot();
            var monitors = DisplayBrightnessHub.MonitorNames;
            var target = DisplayTargets.Resolve(binding, monitors, include);
            var (index, count) = DisplayTargets.Position(target, monitors, include);

            if (target == DisplayTargets.StreamDeck)
            {
                return DisplayBrightnessRenderer.FromStreamDeck(BrightnessHub.Current, _deviceName, Math.Max(0, index - 1), count);
            }

            var snapshot = DisplayBrightnessHub.Get(binding);
            if (!snapshot.Available)
            {
                return snapshot;
            }

            // The override is cosmetic and applied here, at the edge: everything
            // else keeps addressing the screen by the name the platform gave it.
            return snapshot with
            {
                Name = DisplayNames.Resolve(snapshot.Name),
                Index = Math.Max(0, index - 1),
                Count = count,
            };
        }
    }

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

        var delta = payload.Ticks * step;
        if (ResolvedTarget() == DisplayTargets.StreamDeck)
        {
            var state = BrightnessHub.Adjust(delta);
            _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.BrightnessKey, state.Level);
            return;
        }

        if (!DisplayBrightnessHub.Adjust(BindingSnapshot().Binding, delta))
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
                if (ResolvedTarget() == DisplayTargets.StreamDeck)
                {
                    var state = BrightnessHub.Toggle();
                    _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.BrightnessKey, state.Level);
                }
                else if (!DisplayBrightnessHub.Toggle(BindingSnapshot().Binding))
                {
                    _ = Connection.ShowAlert();
                }

                break;

            case GestureNext:
            case GesturePrevious:
                var (binding, include) = BindingSnapshot();
                var target = DisplayTargets.Neighbor(binding, DisplayBrightnessHub.MonitorNames, include, gesture == GestureNext ? 1 : -1);
                if (target is null)
                {
                    _ = Connection.ShowAlert(); // one target, or none: nothing to move to
                    return;
                }

                BindTo(target);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The platform name a target refers to, or null when the target is not a
    /// monitor (the Stream Deck) or nothing is resolved. Custom names key off
    /// this, never off the binding, so "automatic" renames the screen it
    /// actually resolves to.
    /// </summary>
    private static string? ResolvedPlatformName(string? target) =>
        string.IsNullOrEmpty(target) || target == DisplayTargets.StreamDeck ? null : target;

    /// <summary>Point this dial at a target and remember it in the action's settings.</summary>
    private void BindTo(string target)
    {
        JObject settings;
        lock (_gate)
        {
            _binding = target;
            _settings[MonitorKey] = target;
            // Keep the inspector's rename box pointed at the screen now shown,
            // so renaming never lands on the screen the dial just left.
            _settings[RenameKey] = DisplayNames.CustomFor(ResolvedPlatformName(target)) ?? "";
            settings = (JObject)_settings.DeepClone();
        }

        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] display brightness bound to {target}");
        _ = Connection.SetSettingsAsync(settings);
        Push(Current, full: false);
    }

    // -- display --------------------------------------------------------------

    public override void OnTick()
    {
    }

    private void Push(DisplayBrightnessSnapshot snapshot, bool full)
    {
        var isStreamDeck = ResolvedTarget() == DisplayTargets.StreamDeck;
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var frame = DisplayBrightnessRenderer.Render(snapshot, TileFor(isStreamDeck, snapshot.Dimmed || !snapshot.Available));
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

    /// <summary>Monitor or sun tile, lit or dimmed, rendered once each. Call under the gate.</summary>
    private string TileFor(bool streamDeck, bool dimmed)
    {
        var key = $"{(streamDeck ? "deck" : "monitor")}:{(dimmed ? "dim" : "on")}";
        if (!_tileCache.TryGetValue(key, out var dataUri))
        {
            var png = streamDeck ? ArtRenderer.RenderBrightnessTile(dimmed) : ArtRenderer.RenderMonitorTile(dimmed);
            dataUri = ArtRenderer.ToDataUri(png);
            _tileCache[key] = dataUri;
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
            _binding = monitor == PropertyInspectorBridge.AutomaticValue ? DisplayTargets.Automatic : monitor;
            // The setting is honoured only where the deck's screen can actually be
            // driven; elsewhere the deck never enters the cycle, so a profile
            // synced from Windows cannot offer a target that does nothing.
            _includeStreamDeck = BrightnessHub.IsSupported
                && SettingsReader.GetBool(settings, IncludeStreamDeckKey, fallback: true);
            _stepPercent = int.Parse(SettingsReader.GetString(settings, StepKey, "2", Steps));
            _press = SettingsReader.GetString(settings, PressKey, GestureDim, Gestures);
            _tap = SettingsReader.GetString(settings, TapKey, GestureNext, Gestures);
            _hold = SettingsReader.GetString(settings, HoldKey, GesturePrevious, Gestures);
        }

        // A rename typed in the inspector applies to the screen this dial is
        // showing. Written only when it differs from what is stored, so the
        // settings echo that follows a save does not loop.
        var renameTo = SettingsReader.GetString(settings, RenameKey, "").Trim();
        var renameTarget = ResolvedPlatformName(ResolvedTarget());
        if (renameTarget is not null && renameTo != (DisplayNames.CustomFor(renameTarget) ?? ""))
        {
            _ = DisplayNames.SetAsync(Connection, renameTarget, renameTo);
            Push(Current, full: false);
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, MonitorKey, IncludeStreamDeckKey, StepKey, PressKey, TapKey, HoldKey))
        {
            JObject filled;
            lock (_gate)
            {
                _settings[MonitorKey] = _binding.Length == 0 ? PropertyInspectorBridge.AutomaticValue : _binding;
                _settings[IncludeStreamDeckKey] = _includeStreamDeck;
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
        DisplayBrightnessHub.Changed -= _onMonitorChanged;
        DisplayBrightnessHub.MonitorsChanged -= _onMonitorsChanged;
        BrightnessHub.Changed -= _onStreamDeckChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] display brightness disappear");
    }
}
