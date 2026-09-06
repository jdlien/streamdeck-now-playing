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

    private readonly object _gate = new();
    private readonly Action<NowPlayingSnapshot> _onSnapshot;
    private readonly Action<Artwork?> _onArtwork;
    private FeedbackFrame? _lastFrame;
    private DateTimeOffset _lastSkipAt = DateTimeOffset.MinValue;
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
        MediaHub.Attach();
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
        if (payload.Ticks == 0 || payload.IsDialPressed)
        {
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
    public override void DialDown(DialPayload payload) => Fire("toggle");

    public override void DialUp(DialPayload payload)
    {
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

    private void Push(NowPlayingSnapshot snapshot, bool full)
    {
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var frame = FeedbackRenderer.Render(snapshot, DateTimeOffset.UtcNow, IconOverrideFor(snapshot.State));
            payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;
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
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, ShowArtKey))
        {
            var filled = settings is null ? new JObject() : (JObject)settings.DeepClone();
            filled[ShowArtKey] = _showArt;
            _ = Connection.SetSettingsAsync(filled);
        }
    }

    public override void Dispose()
    {
        MediaHub.SnapshotChanged -= _onSnapshot;
        MediaHub.ArtworkChanged -= _onArtwork;
        MediaHub.Detach();
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] dial disappear");
    }
}
