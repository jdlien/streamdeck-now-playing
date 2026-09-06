using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The display brightness dial, in the shared layout: a monitor's brightness
/// over DDC/CI. Turn to adjust; press or tap either dims to the monitor's
/// minimum and back or selects the next monitor, per setting; a long touch
/// always selects the next monitor. The strip shows the requested level at
/// once; the monitor catches up a round trip later.
/// </summary>
[PluginActionId("com.jdlien.now-playing.display-brightness")]
public sealed class DisplayBrightnessAction : EncoderBase
{
    private const string LayoutPath = "layouts/display-brightness.json";
    private const string StepKey = "step";
    private const string PressKey = "pressAction";
    private const string PressDim = "dim";
    private const string PressNext = "next";
    private static readonly string[] Steps = ["1", "2", "5", "10"];
    private static readonly string[] PressActions = [PressDim, PressNext];

    private readonly object _gate = new();
    private readonly Action<DisplayBrightnessSnapshot> _onChanged;
    private readonly Dictionary<bool, string> _tileCache = new();
    private FeedbackFrame? _lastFrame;
    private int _stepPercent = 2;
    private string _pressAction = PressDim;

    public DisplayBrightnessAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] display brightness appear: context {connection.ContextId}");

        ApplySettings(payload.Settings, writeBackDefaults: true);

        _onChanged = snapshot =>
        {
            Push(snapshot, full: false);
            if (snapshot.Available)
            {
                _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.DisplayMonitorKey, snapshot.Name);
            }
        };
        DisplayBrightnessHub.Changed += _onChanged;
        _ = connection.GetGlobalSettingsAsync();
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

    public override void DialDown(DialPayload payload) => Press();

    public override void DialUp(DialPayload payload)
    {
    }

    /// <summary>A short tap does what the press does; a hold always moves to the next monitor.</summary>
    public override void TouchPress(TouchpadPressPayload payload)
    {
        if (payload.IsLongPress)
        {
            NextMonitor();
            return;
        }

        Press();
    }

    private void Press()
    {
        string action;
        lock (_gate)
        {
            action = _pressAction;
        }

        if (action == PressNext)
        {
            NextMonitor();
            return;
        }

        if (!DisplayBrightnessHub.Toggle())
        {
            _ = Connection.ShowAlert();
        }
    }

    private void NextMonitor()
    {
        if (!DisplayBrightnessHub.NextMonitor())
        {
            _ = Connection.ShowAlert(); // one monitor, or none: nothing to cycle to
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
            _pressAction = SettingsReader.GetString(settings, PressKey, PressDim, PressActions);
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, StepKey, PressKey))
        {
            var filled = settings is null ? new JObject() : (JObject)settings.DeepClone();
            filled[StepKey] = _stepPercent.ToString();
            filled[PressKey] = _pressAction;
            _ = Connection.SetSettingsAsync(filled);
        }
    }

    public override void Dispose()
    {
        DisplayBrightnessHub.Changed -= _onChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] display brightness disappear");
    }
}
