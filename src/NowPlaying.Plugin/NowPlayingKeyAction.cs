using BarRaider.SdTools;
using Newtonsoft.Json.Linq;
using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>
/// The key action, for decks without a dial and for anyone who wants the
/// art on a key: album art with a play/pause badge, the title and artist
/// drawn into the image, a configurable press, and a long press for a
/// second command. The Stream Deck app already ships plain transport keys,
/// so this key's value is showing what is playing.
/// </summary>
[PluginActionId("com.jdlien.now-playing.key")]
public sealed class NowPlayingKeyAction : SafeKeypadBase
{
    private const string PressKey = "pressAction";
    private const string LongPressKey = "longPressAction";
    private const string ShowArtKey = "showArt";
    private const string ShowTitleKey = "showTitle";
    private static readonly string[] Commands = ["toggle", "next", "previous", "none"];

    /// <summary>A press held at least this long runs the long-press command instead.</summary>
    private static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly FeedbackPump _pump;
    private const string ImageEntry = "image";
    private readonly Action<NowPlayingSnapshot> _onSnapshot;
    private readonly Action<Artwork?> _onArtwork;
    private string _pressCommand = "toggle";
    private string _longPressCommand = "next";
    private bool _showArt = true;
    private bool _showTitle = true;
    private Artwork? _artwork;
    private DateTimeOffset? _pressedAt;
    private (PlaybackState State, string? ArtKey, bool ShowArt, string Title, string Artist)? _lastImage;

    public NowPlayingKeyAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] key appear: context {connection.ContextId}");

        // The same ordered sender the dials use, so key images cannot land out
        // of order, a failed one is re-rendered, and a send that never returns
        // is caught by the pump's hard limit rather than silencing the plugin.
        _pump = new FeedbackPump("key", Compose, frame => Connection.SetImageAsync((byte[])frame[ImageEntry], null, true));

        ApplySettings(payload.Settings, writeBackDefaults: true);
        PropertyInspectorBridge.Attach(connection);
        _ = SendWatchdog.Watch(connection.GetGlobalSettingsAsync(), "global settings request");

        _onSnapshot = _ => Refresh();
        // The event says the artwork changed; what it changed to is read from
        // the service, never taken from the event. Events are raised outside
        // the service's lock, so two can arrive out of order, and the cached
        // payload of the late one would otherwise stay on the dial for good.
        _onArtwork = _ =>
        {
            lock (_gate)
            {
                _artwork = MediaHub.CurrentArtwork;
            }

            Refresh();
        };
        MediaHub.SnapshotChanged += _onSnapshot;
        MediaHub.ArtworkChanged += _onArtwork;
        MediaHub.Attach();
        _artwork = MediaHub.CurrentArtwork;

        connection.OnSystemDidWakeUp += (_, _) => _ = MediaHub.RefreshAsync(full: true);
        connection.OnDeviceDidConnect += (_, _) =>
        {
            _ = MediaHub.RefreshAsync(full: false);
            Refresh(force: true);
        };

        Refresh(force: true);
    }

    // -- input --------------------------------------------------------------

    protected override void OnKeyPressed(KeyPayload payload)
    {
        lock (_gate)
        {
            _pressedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Everything happens on release so a long press does not also fire the short command.</summary>
    protected override void OnKeyReleased(KeyPayload payload)
    {
        string command;
        lock (_gate)
        {
            var held = _pressedAt is { } pressedAt ? DateTimeOffset.UtcNow - pressedAt : TimeSpan.Zero;
            _pressedAt = null;
            command = held >= LongPress ? _longPressCommand : _pressCommand;
        }

        _ = RunAsync(command);
    }

    private async Task RunAsync(string command)
    {
        try
        {
            var accepted = await MediaHub.RunAsync(command);
            Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] key {command}: {(accepted ? "accepted" : "rejected")}");
            if (!accepted)
            {
                await SendWatchdog.Watch(Connection.ShowAlert(), "alert");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.ERROR, $"[action] key {command} failed: {ex.Message}");
        }
    }

    // -- display --------------------------------------------------------------

    protected override void OnTickGuarded()
    {
    }

    /// <summary>Re-render the image if anything it depends on changed; see <see cref="FeedbackPump"/>.</summary>
    private void Refresh(bool force = false) => _pump.Request(force);

    /// <summary>The image to send, or nothing when it would be unchanged. Called by the pump only, one pass at a time.</summary>
    private Dictionary<string, object> Compose(bool force)
    {
        var snapshot = MediaHub.Current;
        lock (_gate)
        {
            var title = _showTitle ? snapshot.Title : "";
            var artist = _showTitle ? snapshot.Artist : "";
            var imageKey = (snapshot.State, _showArt ? _artwork?.Key : null, _showArt, title, artist);
            if (!force && _lastImage == imageKey)
            {
                return [];
            }

            try
            {
                var png = ArtRenderer.RenderKey(snapshot.State, _showArt ? _artwork?.Bytes : null, title, artist);
                _lastImage = imageKey;
                return new Dictionary<string, object> { [ImageEntry] = png };
            }
            catch (Exception ex)
            {
                Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] key render failed: {ex.Message}");
                return [];
            }
        }
    }

    // -- settings and lifetime ------------------------------------------------

    protected override void OnReceivedSettings(ReceivedSettingsPayload payload)
    {
        ApplySettings(payload.Settings, writeBackDefaults: false);
        Refresh();
    }

    protected override void OnReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) =>
        MediaHub.SetPreferredAppId(PropertyInspectorBridge.PreferredAppFrom(payload.Settings));

    private void ApplySettings(JObject? settings, bool writeBackDefaults)
    {
        lock (_gate)
        {
            _pressCommand = SettingsReader.GetString(settings, PressKey, "toggle", Commands);
            _longPressCommand = SettingsReader.GetString(settings, LongPressKey, "next", Commands);
            _showArt = SettingsReader.GetBool(settings, ShowArtKey, fallback: true);
            _showTitle = SettingsReader.GetBool(settings, ShowTitleKey, fallback: true);
        }

        if (writeBackDefaults && SettingsReader.IsMissingAny(settings, PressKey, LongPressKey, ShowArtKey, ShowTitleKey))
        {
            var filled = settings is null ? new JObject() : (JObject)settings.DeepClone();
            filled[PressKey] = _pressCommand;
            filled[LongPressKey] = _longPressCommand;
            filled[ShowArtKey] = _showArt;
            filled[ShowTitleKey] = _showTitle;
            _ = SendWatchdog.Watch(Connection.SetSettingsAsync(filled), "settings");
        }
    }

    protected override void OnDispose()
    {
        _pump.Stop();
        MediaHub.SnapshotChanged -= _onSnapshot;
        MediaHub.ArtworkChanged -= _onArtwork;
        MediaHub.Detach();
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] key disappear");
    }
}
