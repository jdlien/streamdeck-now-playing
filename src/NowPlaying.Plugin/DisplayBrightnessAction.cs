using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The display brightness dial, in the shared layout: the primary monitor's
/// brightness over DDC/CI. Turn to adjust, press or tap to dim to the
/// monitor's minimum and back. The strip shows the requested level at once;
/// the monitor catches up a round trip later.
/// </summary>
[PluginActionId("com.jdlien.now-playing.display-brightness")]
public sealed class DisplayBrightnessAction : EncoderBase
{
    private const string LayoutPath = "layouts/display-brightness.json";
    private const string StepKey = "step";
    private static readonly string[] Steps = ["1", "2", "5", "10"];

    private readonly object _gate = new();
    private readonly Action<DisplayBrightnessSnapshot> _onChanged;
    private readonly Dictionary<bool, string> _tileCache = new();
    private FeedbackFrame? _lastFrame;
    private int _stepPercent = 2;

    public DisplayBrightnessAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] display brightness appear: context {connection.ContextId}");

        ApplySettings(payload.Settings, writeBackDefaults: true);

        _onChanged = snapshot => Push(snapshot, full: false);
        DisplayBrightnessHub.Changed += _onChanged;
        DisplayBrightnessHub.Attach();

        connection.OnSystemDidWakeUp += (_, _) => DisplayBrightnessHub.Refresh();
        connection.OnDeviceDidConnect += (_, _) => Push(DisplayBrightnessHub.Current, full: true);

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
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] display setFeedbackLayout failed: {ex.Message}");
        }

        Push(DisplayBrightnessHub.Current, full: true);
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

        if (!DisplayBrightnessHub.Adjust(payload.Ticks * step))
        {
            _ = Connection.ShowAlert();
        }
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
        if (!DisplayBrightnessHub.Toggle())
        {
            _ = Connection.ShowAlert();
        }
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

    public override void ReceivedSettings(ReceivedSettingsPayload payload) => ApplySettings(payload.Settings, writeBackDefaults: false);

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) => GlobalSettingsStore.Apply(payload.Settings);

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
            _ = Connection.SetSettingsAsync(filled);
        }
    }

    public override void Dispose()
    {
        DisplayBrightnessHub.Changed -= _onChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] display brightness disappear");
    }
}
