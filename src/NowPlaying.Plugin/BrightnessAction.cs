using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The brightness dial, in the shared layout: turn to set the Stream Deck's
/// screen brightness, press or tap to toggle it to 0 and back. The device
/// cannot report its brightness, so the level shown is the one this plugin
/// last set, persisted in global settings and re-applied after wake and
/// reconnect.
/// </summary>
[PluginActionId("com.jdlien.now-playing.brightness")]
public sealed class BrightnessAction : EncoderBase
{
    private const string LayoutPath = "layouts/brightness.json";
    private const string StepKey = "step";
    private static readonly string[] Steps = ["1", "2", "5", "10"];

    private readonly object _gate = new();
    private readonly Action<BrightnessState> _onChanged;
    private readonly string _deviceName;
    private readonly Dictionary<bool, string> _tileCache = new();
    private FeedbackFrame? _lastFrame;
    private int _stepPercent = 5;

    public BrightnessAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] brightness appear: context {connection.ContextId}");

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

        _onChanged = state => Push(state, full: false);
        BrightnessHub.Changed += _onChanged;
        BrightnessHub.Attach();
        _ = connection.GetGlobalSettingsAsync();

        connection.OnSystemDidWakeUp += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] brightness: re-applying after wake");
            BrightnessHub.Reapply();
        };
        connection.OnDeviceDidConnect += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] brightness: re-applying after device connect");
            BrightnessHub.Reapply();
            Push(BrightnessHub.Current, full: true);
        };

        _ = ApplyLayoutAndPushAsync();
    }

    private async Task ApplyLayoutAndPushAsync()
    {
        try
        {
            await Connection.SetFeedbackLayoutAsync(LayoutPath);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] brightness setFeedbackLayout failed: {ex.Message}");
        }

        Push(BrightnessHub.Current, full: true);
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

        var state = BrightnessHub.Adjust(payload.Ticks * step);
        _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.BrightnessKey, state.Level);
    }

    public override void DialDown(DialPayload payload) => Toggle();

    public override void DialUp(DialPayload payload)
    {
    }

    public override void TouchPress(TouchpadPressPayload payload)
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

    public override void OnTick()
    {
    }

    private void Push(BrightnessState state, bool full)
    {
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var frame = BrightnessRenderer.Render(state, _deviceName, TileFor(state.Off));
            payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;
        }

        if (payload.Count == 0)
        {
            return;
        }

        _ = SendAsync(payload);
    }

    /// <summary>The two sun tiles, rendered once each. Call under the gate.</summary>
    private string TileFor(bool off)
    {
        if (!_tileCache.TryGetValue(off, out var dataUri))
        {
            dataUri = ArtRenderer.ToDataUri(ArtRenderer.RenderBrightnessTile(off));
            _tileCache[off] = dataUri;
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
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] brightness setFeedback failed: {ex.Message}");
        }
    }

    // -- settings and lifetime ------------------------------------------------

    public override void ReceivedSettings(ReceivedSettingsPayload payload) => ApplySettings(payload.Settings, writeBackDefaults: false);

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) => GlobalSettingsStore.Apply(payload.Settings);

    private void ApplySettings(JObject? settings, bool writeBackDefaults)
    {
        lock (_gate)
        {
            _stepPercent = int.Parse(SettingsReader.GetString(settings, StepKey, "5", Steps));
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, StepKey))
        {
            var filled = settings is null ? new JObject() : (JObject)settings.DeepClone();
            filled[StepKey] = _stepPercent.ToString();
            _ = Connection.SetSettingsAsync(filled);
        }
    }

    public override void Dispose()
    {
        BrightnessHub.Changed -= _onChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] brightness disappear");
    }
}
