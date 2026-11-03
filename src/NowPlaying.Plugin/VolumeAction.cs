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
public sealed class VolumeAction : SafeEncoderBase
{
    private const string LayoutPath = "layouts/volume.json";
    private const string StepKey = "step";
    private static readonly string[] Steps = ["1", "2", "5", "10"];

    private readonly object _gate = new();
    private readonly Action<VolumeSnapshot> _onChanged;
    private readonly FeedbackPump _pump;
    private FeedbackFrame? _lastFrame;
    private int _stepPercent = 2;
    private readonly Dictionary<bool, string> _tileCache = new();

    public VolumeAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] volume appear: context {connection.ContextId}");

        _pump = new FeedbackPump("volume dial", Compose, frame => Connection.SetFeedbackAsync(FeedbackJson.ToJObject(frame)));
        ApplySettings(payload.Settings, writeBackDefaults: true);

        _onChanged = snapshot => Push(full: false);
        VolumeHub.Changed += _onChanged;
        VolumeHub.Attach();

        connection.OnDeviceDidConnect += (_, _) => Push(full: true);

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
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] volume setFeedbackLayout failed: {ex.Message}");
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

        if (!VolumeHub.AdjustBy(payload.Ticks * step / 100f))
        {
            _ = SendWatchdog.Watch(Connection.ShowAlert(), "alert");
        }
    }

    protected override void OnDialDown(DialPayload payload) => ToggleMute();

    protected override void OnDialUp(DialPayload payload)
    {
    }

    protected override void OnTouchPress(TouchpadPressPayload payload)
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
            _ = SendWatchdog.Watch(Connection.ShowAlert(), "alert");
        }
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
        var snapshot = VolumeHub.Current;
        lock (_gate)
        {
            var frame = VolumeRenderer.Render(snapshot, TileFor(snapshot.Muted));
            var payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;
            return payload;
        }
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

    // -- settings and lifetime ------------------------------------------------

    protected override void OnReceivedSettings(ReceivedSettingsPayload payload) => ApplySettings(payload.Settings, writeBackDefaults: false);

    protected override void OnReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
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
            _ = SendWatchdog.Watch(Connection.SetSettingsAsync(filled), "settings");
        }
    }

    protected override void OnDispose()
    {
        _pump.Stop();
        VolumeHub.Changed -= _onChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] volume disappear");
    }
}
