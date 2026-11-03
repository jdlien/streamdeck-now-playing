using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The brightness dial, in the shared layout: turn to set the Stream Deck's
/// screen brightness, press or tap to dim it to a glow and back. The device
/// cannot report its brightness, so the level shown is the one this plugin
/// last set, persisted in global settings and re-applied after wake and
/// reconnect.
/// </summary>
[PluginActionId("com.jdlien.now-playing.brightness")]
public sealed class BrightnessAction : SafeEncoderBase
{
    private const string LayoutPath = "layouts/brightness.json";
    private const string StepKey = "step";
    private static readonly string[] Steps = ["1", "2", "5", "10"];

    private readonly object _gate = new();
    private readonly Action<BrightnessState> _onChanged;
    private readonly string _deviceName;
    private readonly Dictionary<bool, string> _tileCache = new();
    private readonly FeedbackPump _pump;
    private FeedbackFrame? _lastFrame;
    private int _stepPercent = 2;

    public BrightnessAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] brightness appear: context {connection.ContextId}");
        _pump = new FeedbackPump("brightness dial", Compose, frame => Connection.SetFeedbackAsync(FeedbackJson.ToJObject(frame)));

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

        _onChanged = state => Push(full: false);
        BrightnessHub.Changed += _onChanged;
        BrightnessHub.Attach();
        _ = SendWatchdog.Watch(connection.GetGlobalSettingsAsync(), "global settings request");

        connection.OnSystemDidWakeUp += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] brightness: re-applying after wake");
            BrightnessHub.Reapply();
        };
        connection.OnDeviceDidConnect += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] brightness: re-applying after device connect");
            BrightnessHub.Reapply();
            Push(full: true);
        };

        _ = ApplyLayoutAndPushAsync();
    }

    private async Task ApplyLayoutAndPushAsync()
    {
        try
        {
            await SendWatchdog.Watch(Connection.SetFeedbackLayoutAsync(LayoutPath), "layout");
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] brightness setFeedbackLayout failed: {ex.Message}");
        }

        Push(full: true);
    }

    // -- input --------------------------------------------------------------

    protected override void OnDialRotate(DialRotatePayload payload)
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

        var state = BrightnessHub.Adjust(payload.Ticks * step);
        _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.BrightnessKey, state.Level);
    }

    protected override void OnDialDown(DialPayload payload) => Toggle();

    protected override void OnDialUp(DialPayload payload)
    {
    }

    protected override void OnTouchPress(TouchpadPressPayload payload)
    {
        if (payload.IsLongPress)
        {
            return;
        }

        Toggle();
    }

    private void Toggle()
    {
        var state = BrightnessHub.Toggle();
        _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.BrightnessKey, state.Level);
    }

    // -- display --------------------------------------------------------------

    protected override void OnTickGuarded()
    {
    }

    /// <summary>Bring the strip up to date; see <see cref="FeedbackPump"/>.</summary>
    private void Push(bool full) => _pump.Request(full);

    /// <summary>What the strip needs to match the current state. Called by the pump only, one pass at a time.</summary>
    private Dictionary<string, object> Compose(bool full)
    {
        var state = BrightnessHub.Current;
        lock (_gate)
        {
            var frame = BrightnessRenderer.Render(state, _deviceName, TileFor(state.Dimmed));
            var payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;
            return payload;
        }
    }

    /// <summary>The two sun tiles, rendered once each. Call under the gate.</summary>
    private string TileFor(bool dimmed)
    {
        if (!_tileCache.TryGetValue(dimmed, out var dataUri))
        {
            dataUri = ArtRenderer.ToDataUri(ArtRenderer.RenderBrightnessTile(dimmed));
            _tileCache[dimmed] = dataUri;
        }

        return dataUri;
    }

    // -- settings and lifetime ------------------------------------------------

    protected override void OnReceivedSettings(ReceivedSettingsPayload payload) => ApplySettings(payload.Settings, writeBackDefaults: false);

    protected override void OnReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) => GlobalSettingsStore.Apply(payload.Settings);

    private void ApplySettings(JObject? settings, bool writeBackDefaults)
    {
        lock (_gate)
        {
            _stepPercent = int.Parse(SettingsReader.GetString(settings, StepKey, "2", Steps));
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, StepKey))
        {
            var filled = settings is null ? new JObject() : (JObject)settings.DeepClone();
            filled[StepKey] = _stepPercent.ToString();
            _ = SendWatchdog.Watch(Connection.SetSettingsAsync(filled), "settings");
        }
    }

    protected override void OnDispose()
    {
        _pump.Stop();
        BrightnessHub.Changed -= _onChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] brightness disappear");
    }
}
