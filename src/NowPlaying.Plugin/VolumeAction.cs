using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Audio;

namespace NowPlaying.Plugin;

/// <summary>
/// The volume dial: same layout as Now Playing, driven by the default output
/// device's volume. Turn to adjust, press or tap to mute. Unlike track
/// skipping, every tick counts here, so a fast spin moves further.
/// </summary>
[PluginActionId("com.jdlien.now-playing.volume")]
public sealed class VolumeAction : EncoderBase
{
    private const string LayoutPath = "layouts/volume.json";
    private const string StepKey = "step";
    private static readonly string[] Steps = ["1", "2", "5", "10"];

    private readonly object _gate = new();
    private readonly Action<VolumeSnapshot> _onChanged;
    private FeedbackFrame? _lastFrame;
    private int _stepPercent = 2;
    private readonly Dictionary<bool, string> _tileCache = new();

    public VolumeAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] volume appear: context {connection.ContextId}");

        ApplySettings(payload.Settings, writeBackDefaults: true);

        _onChanged = snapshot => Push(snapshot, full: false);
        VolumeHub.Changed += _onChanged;
        VolumeHub.Attach();

        connection.OnDeviceDidConnect += (_, _) => Push(VolumeHub.Current, full: true);

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
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] volume setFeedbackLayout failed: {ex.Message}");
        }

        Push(VolumeHub.Current, full: true);
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

        if (!VolumeHub.AdjustBy(payload.Ticks * step / 100f))
        {
            _ = Connection.ShowAlert();
        }
    }

    public override void DialDown(DialPayload payload) => ToggleMute();

    public override void DialUp(DialPayload payload)
    {
    }

    public override void TouchPress(TouchpadPressPayload payload)
    {
        if (payload.IsLongPress)
        {
            return;
        }

        ToggleMute();
    }

    private void ToggleMute()
    {
        if (!VolumeHub.ToggleMute())
        {
            _ = Connection.ShowAlert();
        }
    }

    // -- display --------------------------------------------------------------

    public override void OnTick()
    {
    }

    private void Push(VolumeSnapshot snapshot, bool full)
    {
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var frame = VolumeRenderer.Render(snapshot, TileFor(snapshot.Muted));
            payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;
        }

        if (payload.Count == 0)
        {
            return;
        }

        _ = SendAsync(payload);
    }

    /// <summary>The two speaker tiles, rendered once each. Call under the gate.</summary>
    private string TileFor(bool muted)
    {
        if (!_tileCache.TryGetValue(muted, out var dataUri))
        {
            dataUri = ArtRenderer.ToDataUri(ArtRenderer.RenderVolumeTile(muted));
            _tileCache[muted] = dataUri;
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
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] volume setFeedback failed: {ex.Message}");
        }
    }

    // -- settings and lifetime ------------------------------------------------

    public override void ReceivedSettings(ReceivedSettingsPayload payload) => ApplySettings(payload.Settings, writeBackDefaults: false);

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
    {
    }

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
        VolumeHub.Changed -= _onChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] volume disappear");
    }
}
