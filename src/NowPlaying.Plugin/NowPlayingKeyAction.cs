using BarRaider.SdTools;
using Newtonsoft.Json.Linq;
using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>
/// The key action, for decks without a dial and for anyone who wants the
/// art on a key: album art with a play/pause badge, the title in the key's
/// own title field, a configurable press, and a long press for a second
/// command. The Stream Deck app already ships plain transport keys, so this
/// key's value is showing what is playing.
/// </summary>
[PluginActionId("com.jdlien.now-playing.key")]
public sealed class NowPlayingKeyAction : KeypadBase
{
    private const string PressKey = "pressAction";
    private const string LongPressKey = "longPressAction";
    private const string ShowArtKey = "showArt";
    private const string ShowTitleKey = "showTitle";
    private static readonly string[] Commands = ["toggle", "next", "previous", "none"];

    /// <summary>A press held at least this long runs the long-press command instead.</summary>
    private static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Action<NowPlayingSnapshot> _onSnapshot;
    private readonly Action<Artwork?> _onArtwork;
    private string _pressCommand = "toggle";
    private string _longPressCommand = "next";
    private bool _showArt = true;
    private bool _showTitle = true;
    private Artwork? _artwork;
    private DateTimeOffset? _pressedAt;
    private (PlaybackState State, string? ArtKey, bool ShowArt)? _lastImage;
    private string? _lastTitle;

    public NowPlayingKeyAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] key appear: context {connection.ContextId}");

        ApplySettings(payload.Settings, writeBackDefaults: true);
        PropertyInspectorBridge.Attach(connection);
        _ = connection.GetGlobalSettingsAsync();

        _onSnapshot = _ => Refresh();
        _onArtwork = artwork =>
        {
            lock (_gate)
            {
                _artwork = artwork;
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

    public override void KeyPressed(KeyPayload payload)
    {
        lock (_gate)
        {
            _pressedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Everything happens on release so a long press does not also fire the short command.</summary>
    public override void KeyReleased(KeyPayload payload)
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
                await Connection.ShowAlert();
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.ERROR, $"[action] key {command} failed: {ex.Message}");
        }
    }

    // -- display --------------------------------------------------------------

    public override void OnTick()
    {
    }

    /// <summary>Re-render the image and title if what they depend on changed.</summary>
    private void Refresh(bool force = false)
    {
        var snapshot = MediaHub.Current;
        byte[]? png = null;
        string? title = null;

        lock (_gate)
        {
            var imageKey = (snapshot.State, _showArt ? _artwork?.Key : null, _showArt);
            if (force || _lastImage != imageKey)
            {
                try
                {
                    png = ArtRenderer.RenderKey(snapshot.State, _showArt ? _artwork?.Bytes : null);
                    _lastImage = imageKey;
                }
                catch (Exception ex)
                {
                    Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] key art render failed: {ex.Message}");
                }
            }

            var wantedTitle = _showTitle && snapshot.State != PlaybackState.None ? snapshot.Title : "";
            if (force || wantedTitle != _lastTitle)
            {
                title = wantedTitle;
                _lastTitle = wantedTitle;
            }
        }

        if (png is not null)
        {
            _ = SendImageAsync(png);
        }

        if (title is not null)
        {
            _ = SendTitleAsync(title);
        }
    }

    private async Task SendImageAsync(byte[] png)
    {
        try
        {
            await Connection.SetImageAsync(png, null, true);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] setImage failed: {ex.Message}");
        }
    }

    private async Task SendTitleAsync(string title)
    {
        try
        {
            await Connection.SetTitleAsync(title, null);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] setTitle failed: {ex.Message}");
        }
    }

    // -- settings and lifetime ------------------------------------------------

    public override void ReceivedSettings(ReceivedSettingsPayload payload)
    {
        ApplySettings(payload.Settings, writeBackDefaults: false);
        Refresh();
    }

    public override void ReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload) =>
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
            _ = Connection.SetSettingsAsync(filled);
        }
    }

    public override void Dispose()
    {
        MediaHub.SnapshotChanged -= _onSnapshot;
        MediaHub.ArtworkChanged -= _onArtwork;
        MediaHub.Detach();
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] key disappear");
    }
}
