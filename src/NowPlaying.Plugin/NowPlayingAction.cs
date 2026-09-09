using NowPlaying.Audio;
using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>
/// The dial action: a dial plus its touch-strip segment. StreamDeck-Tools
/// creates an instance on willAppear and disposes it on willDisappear, so
/// the constructor is "appear" and Dispose is "disappear".
///
/// Display: README section 6. Input: section 7. Recovery: section 8.
/// </summary>
[PluginActionId("com.jdlien.now-playing.dial")]
public sealed class NowPlayingAction : EncoderBase
{
    /// <summary>Minimum gap between skips. Extra rotate events inside it are dropped, never queued.</summary>
    private static readonly TimeSpan SkipInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Path of the custom layout, relative to the plugin folder. Also named in the manifest.</summary>
    private const string LayoutPath = "layouts/now-playing.json";

    private const string ShowArtKey = "showArt";
    private const string RotateKey = "rotate";
    private const string VolumeStepKey = "volumeStep";

    private const string RotateTrack = DialGestures.TrackMode;
    private const string RotateVolume = DialGestures.VolumeMode;

    private static readonly string[] RotateModes = [RotateTrack, RotateVolume];
    private static readonly string[] VolumeSteps = ["1", "2", "5", "10"];

    private readonly object _gate = new();
    private readonly Action<NowPlayingSnapshot> _onSnapshot;
    private readonly Action<Artwork?> _onArtwork;
    private readonly Action<VolumeSnapshot> _onVolume;
    private FeedbackFrame? _lastFrame;
    private DateTimeOffset _lastSkipAt = DateTimeOffset.MinValue;
    private string _rotate = RotateTrack;
    private int _volumeStep = 2;

    /// <summary>
    /// Set when the dial is turned while held, so releasing it does not also
    /// toggle play/pause. Only used in volume mode: in track mode the toggle
    /// still fires on press, which is what the dial has always done.
    /// </summary>
    private bool _rotatedWhilePressed;

    /// <summary>
    /// While set, the strip shows volume instead of the track. Turning a dial
    /// and watching a progress bar that has nothing to do with what you are
    /// changing is worse than useless, so a volume turn takes the strip over
    /// briefly and then hands it back.
    /// </summary>
    private DateTimeOffset _volumeShownUntil = DateTimeOffset.MinValue;

    /// <summary>How long a volume turn keeps the strip after the last movement.</summary>
    private static readonly TimeSpan VolumeOverlay = TimeSpan.FromSeconds(2);

    private const string VolumeIconKey = "volumeIcon";
    private const string VolumeTextKey = "volumeText";

    /// <summary>Resting opacity of the volume badge, matching the monitor badge's understatement.</summary>
    private const double BadgeDim = 0.45;

    /// <summary>What the badge is worth looking at.</summary>
    private const double BadgeBright = 1.0;

    /// <summary>Last badge state sent, so an unchanged badge is not re-sent every tick.</summary>
    private (bool Shown, string Text, double Opacity)? _lastBadge;
    private bool _showArt = true;
    private Artwork? _artwork;
    private (string ArtKey, PlaybackState State, string DataUri)? _iconCache;

    public NowPlayingAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] dial appear: context {connection.ContextId}");

        ApplySettings(payload.Settings, writeBackDefaults: true);
        PropertyInspectorBridge.Attach(connection);
        _ = connection.GetGlobalSettingsAsync();

        _onSnapshot = snapshot => Push(snapshot, full: false);
        _onArtwork = artwork =>
        {
            lock (_gate)
            {
                _artwork = artwork;
            }

            Push(MediaHub.Current, full: false);
        };
        MediaHub.SnapshotChanged += _onSnapshot;
        MediaHub.ArtworkChanged += _onArtwork;
        _onVolume = _ => Push(MediaHub.Current, full: false);
        VolumeHub.Changed += _onVolume;
        MediaHub.Attach();
        VolumeHub.Attach();   // the dial can be set to control volume; see RotateVolume
        _artwork = MediaHub.CurrentArtwork;

        connection.OnSystemDidWakeUp += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] system woke up: full media refresh");
            _ = MediaHub.RefreshAsync(full: true);
        };
        connection.OnDeviceDidConnect += (_, _) =>
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, "[action] device connected: re-push");
            _ = MediaHub.RefreshAsync(full: false);
            Push(MediaHub.Current, full: true);
        };

        _ = ApplyLayoutAndPushAsync();
    }

    /// <summary>
    /// Re-apply the layout file, then push everything. Setting the layout
    /// explicitly means an edited layout takes effect on a plugin restart, and
    /// the full push follows because the strip may show anything from before.
    /// </summary>
    private async Task ApplyLayoutAndPushAsync()
    {
        try
        {
            await Connection.SetFeedbackLayoutAsync(LayoutPath);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] setFeedbackLayout failed: {ex.Message}");
        }

        Push(MediaHub.Current, full: true);
    }

    // -- input --------------------------------------------------------------

    /// <summary>
    /// One skip per event in the sign direction regardless of the tick count,
    /// which can exceed one on a fast spin. Rotation while pressed is
    /// reserved and ignored.
    /// </summary>
    public override void DialRotate(DialRotatePayload payload)
    {
        if (payload.Ticks == 0)
        {
            return;
        }

        string mode;
        int step;
        lock (_gate)
        {
            mode = _rotate;
            step = _volumeStep;
            if (payload.IsDialPressed)
            {
                _rotatedWhilePressed = true;
            }
        }

        switch (DialGestures.OnRotate(mode, payload.IsDialPressed))
        {
            case DialTurn.Ignore:
                return;

            case DialTurn.Volume:
                if (!VolumeHub.AdjustBy(payload.Ticks * step / 100f))
                {
                    _ = Connection.ShowAlert();
                    return;
                }

                lock (_gate)
                {
                    _volumeShownUntil = DateTimeOffset.UtcNow + VolumeOverlay;
                }

                Push(MediaHub.Current, full: false);
                return;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (now - _lastSkipAt < SkipInterval)
            {
                return;
            }

            _lastSkipAt = now;
        }

        Fire(payload.Ticks > 0 ? "next" : "previous");
    }

    /// <summary>Toggle once per press. DialUp is deliberately a no-op.</summary>
    public override void DialDown(DialPayload payload)
    {
        lock (_gate)
        {
            _rotatedWhilePressed = false;
            if (!DialGestures.TogglesOnPress(_rotate))
            {
                return;   // decided on release, once it is known whether this was a turn
            }
        }

        Fire("toggle");
    }

    public override void DialUp(DialPayload payload)
    {
        lock (_gate)
        {
            if (!DialGestures.TogglesOnRelease(_rotate, _rotatedWhilePressed))
            {
                return;   // track mode already toggled on press; a turn is not a press
            }
        }

        Fire("toggle");
    }

    /// <summary>A short tap toggles. A hold is a distinct gesture and is ignored for now.</summary>
    public override void TouchPress(TouchpadPressPayload payload)
    {
        if (payload.IsLongPress)
        {
            return;
        }

        Fire("toggle");
    }

    private void Fire(string command) => _ = RunAsync(command);

    private async Task RunAsync(string command)
    {
        try
        {
            var accepted = await MediaHub.RunAsync(command);
            Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] dial {command}: {(accepted ? "accepted" : "rejected")}");
            if (!accepted)
            {
                await Connection.ShowAlert();
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.ERROR, $"[action] dial {command} failed: {ex.Message}");
        }
    }

    // -- display --------------------------------------------------------------

    /// <summary>
    /// StreamDeck-Tools calls this about once a second. While playing, the
    /// extrapolated position moves the bar; otherwise the diff is empty and
    /// nothing is sent.
    /// </summary>
    public override void OnTick() => Push(MediaHub.Current, full: false);

    /// <summary>
    /// The speaker badge, diffed separately because it is not part of the
    /// shared six-item frame the other dials use. Call under the gate.
    /// </summary>
    private void AddBadge(Dictionary<string, object> payload, VolumeSnapshot? volume, bool turning, bool full)
    {
        // A device with no software volume has no level worth showing, so the
        // badge stays hidden rather than reading 0%.
        var shown = volume is { HasDevice: true, CanSetVolume: true };
        var text = shown ? $"{volume!.Percent}%" : "";
        var opacity = turning ? BadgeBright : BadgeDim;
        var next = (shown, text, opacity);

        if (!full && _lastBadge == next)
        {
            return;
        }

        _lastBadge = next;
        payload[VolumeIconKey] = new Dictionary<string, object> { ["enabled"] = shown, ["opacity"] = opacity };
        payload[VolumeTextKey] = new Dictionary<string, object> { ["value"] = text, ["opacity"] = opacity };
    }

    private void Push(NowPlayingSnapshot snapshot, bool full)
    {
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var frame = FeedbackRenderer.Render(snapshot, now, IconOverrideFor(snapshot.State));

            // In volume mode the badge sits under the bar between the two times,
            // dim, so the level is readable without displacing anything. Turning
            // brings it up and lends it the bar; the times stay put throughout,
            // which is the point -- an overlay that replaced them lost the track
            // position for as long as it was up.
            var volumeMode = _rotate == RotateVolume;
            var volume = volumeMode ? VolumeHub.Current : null;
            var turning = volumeMode && now < _volumeShownUntil;

            if (turning && volume is { CanSetVolume: true })
            {
                frame = frame with
                {
                    BarEnabled = true,
                    BarValue = volume.Percent * FeedbackRenderer.BarRange / 100,
                };
            }

            payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;
            AddBadge(payload, volume, turning, full);
        }

        if (payload.Count == 0)
        {
            return;
        }

        _ = SendAsync(payload);
    }

    /// <summary>The art tile as a data URI when art is wanted and available; cached per (art, state). Call under the gate.</summary>
    private string? IconOverrideFor(PlaybackState state)
    {
        if (!_showArt || _artwork is null || state == PlaybackState.None)
        {
            return null;
        }

        if (_iconCache is { } cached && cached.ArtKey == _artwork.Key && cached.State == state)
        {
            return cached.DataUri;
        }

        try
        {
            var dataUri = ArtRenderer.ToDataUri(ArtRenderer.RenderDialIcon(state, _artwork.Bytes));
            _iconCache = (_artwork.Key, state, dataUri);
            return dataUri;
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] dial art render failed: {ex.Message}");
            return null;
        }
    }

    private async Task SendAsync(Dictionary<string, object> payload)
    {
        try
        {
            await Connection.SetFeedbackAsync(FeedbackJson.ToJObject(payload));
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] setFeedback failed: {ex.Message}");
        }
    }

    // -- settings and lifetime ------------------------------------------------

    public override void ReceivedSettings(ReceivedSettingsPayload payload)
    {
        ApplySettings(payload.Settings, writeBackDefaults: false);
        Push(MediaHub.Current, full: false);
    }

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) =>
        MediaHub.SetPreferredAppId(PropertyInspectorBridge.PreferredAppFrom(payload.Settings));

    private void ApplySettings(JObject? settings, bool writeBackDefaults)
    {
        lock (_gate)
        {
            _showArt = SettingsReader.GetBool(settings, ShowArtKey, fallback: true);
            _rotate = SettingsReader.GetString(settings, RotateKey, RotateTrack, RotateModes);
            _volumeStep = int.Parse(SettingsReader.GetString(settings, VolumeStepKey, "2", VolumeSteps));
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, ShowArtKey, RotateKey, VolumeStepKey))
        {
            var filled = settings is null ? new JObject() : (JObject)settings.DeepClone();
            filled[ShowArtKey] = _showArt;
            filled[RotateKey] = _rotate;
            filled[VolumeStepKey] = _volumeStep.ToString();
            _ = Connection.SetSettingsAsync(filled);
        }
    }

    public override void Dispose()
    {
        VolumeHub.Changed -= _onVolume;
        MediaHub.SnapshotChanged -= _onSnapshot;
        MediaHub.ArtworkChanged -= _onArtwork;
        MediaHub.Detach();
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] dial disappear");
    }
}
