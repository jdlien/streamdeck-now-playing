using BarRaider.SdTools;
using BarRaider.SdTools.Payloads;
using Newtonsoft.Json.Linq;
using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// The display brightness dial, in the shared layout. Each instance binds
/// to a target of its own: a monitor over DDC/CI (automatic = the primary),
/// or the Stream Deck's own screen. Turn to adjust; press, tap, and hold
/// each run a configurable gesture: dim and restore, next target, previous
/// target, or nothing. The Stream Deck can be included in the cycle, which
/// makes this one dial cover every screen on the desk.
/// </summary>
[PluginActionId("com.jdlien.now-playing.display-brightness")]
public sealed class DisplayBrightnessAction : SafeEncoderBase
{
    private const string LayoutPath = "layouts/display-brightness.json";
    private const string MonitorKey = "monitor";
    private const string StepKey = "step";
    private const string PressKey = "pressAction";
    private const string TapKey = "tapAction";
    private const string HoldKey = "holdAction";
    private const string IncludeStreamDeckKey = "includeStreamDeck";
    private const string RenameKey = "renameTo";
    private const string GestureDim = "dim";
    private const string GestureNext = "next";
    private const string GesturePrevious = "previous";
    private const string GestureNone = "none";
    private static readonly string[] Steps = ["1", "2", "5", "10"];
    private static readonly string[] Gestures = [GestureDim, GestureNext, GesturePrevious, GestureNone];

    private readonly object _gate = new();
    private readonly Action<string, DisplayBrightnessSnapshot> _onMonitorChanged;
    private readonly Action _onMonitorsChanged;
    private readonly Action<BrightnessState> _onStreamDeckChanged;
    private readonly Action _onNamesChanged;
    private readonly Dictionary<string, string> _tileCache = new();
    private readonly FeedbackPump _pump;
    private readonly string _deviceName;
    private JObject _settings = new();
    private bool _settingsSeen;

    /// <summary>
    /// The name box's text for the screen this dial just left, and until when
    /// an update still carrying it is taken as stale. The correction to the
    /// box takes a round trip to reach the inspector, and any other control
    /// changed inside it sends the old text again -- now against an unchanged
    /// binding, which reads exactly like typing it.
    ///
    /// The window is that round trip and little more: milliseconds, with a
    /// margin. Longer, and it swallowed genuine edits -- clearing the new
    /// screen's name straight after switching to it was put back -- since a
    /// person cannot see the corrected box, decide, and edit it this fast.
    /// </summary>
    private (string Text, DateTimeOffset Until)? _staleName;

    private static readonly TimeSpan StaleNameWindow = TimeSpan.FromMilliseconds(500);
    private FeedbackFrame? _lastFrame;
    private string? _lastBadge;
    private string _binding = DisplayTargets.Automatic;
    private bool _includeStreamDeck = true;
    private int _stepPercent = 2;
    private string _press = GestureDim;
    private string _tap = GestureNext;
    private string _hold = GesturePrevious;

    public DisplayBrightnessAction(SDConnection connection, InitialPayload payload)
        : base(connection, payload)
    {
        Logger.Instance.LogMessage(TracingLevel.INFO, $"[action] display brightness appear: context {connection.ContextId}");
        _pump = new FeedbackPump("display dial", Compose, frame => Connection.SetFeedbackAsync(FeedbackJson.ToJObject(frame)));

        string? deviceType = null;
        try
        {
            deviceType = connection.DeviceInfo()?.Type.ToString();
        }
        catch
        {
        }

        _deviceName = BrightnessRenderer.DeviceName(deviceType);

        ApplySettings(payload.Settings, initial: true);
        PropertyInspectorBridge.Attach(connection);

        _onMonitorChanged = (name, _) =>
        {
            if (string.Equals(name, ResolvedTarget(), StringComparison.OrdinalIgnoreCase))
            {
                Push(full: false);
            }
        };
        _onMonitorsChanged = () =>
        {
            SyncNameBox();
            Push(full: false);
        };
        _onStreamDeckChanged = _ =>
        {
            if (ResolvedTarget() == DisplayTargets.StreamDeck)
            {
                Push(full: false);
            }
        };
        _onNamesChanged = () =>
        {
            SyncNameBox();
            Push(full: false);
        };
        DisplayBrightnessHub.Changed += _onMonitorChanged;
        DisplayBrightnessHub.MonitorsChanged += _onMonitorsChanged;
        BrightnessHub.Changed += _onStreamDeckChanged;
        DisplayNames.Changed += _onNamesChanged;
        DisplayBrightnessHub.Attach();
        BrightnessHub.Attach();
        _ = SendWatchdog.Watch(connection.GetGlobalSettingsAsync(), "global settings request");

        connection.OnSystemDidWakeUp += (_, _) =>
        {
            DisplayBrightnessHub.Refresh();
            BrightnessHub.Reapply();
        };
        connection.OnDeviceDidConnect += (_, _) =>
        {
            BrightnessHub.Reapply();
            Push(full: true);
        };

        _ = ApplyLayoutAndPushAsync();
    }

    // -- the target -----------------------------------------------------------

    private (string Binding, bool IncludeStreamDeck) BindingSnapshot()
    {
        lock (_gate)
        {
            return (_binding, _includeStreamDeck);
        }
    }

    /// <summary>The concrete target this dial controls right now: a monitor name, the Stream Deck, or null.</summary>
    private string? ResolvedTarget()
    {
        var (binding, include) = BindingSnapshot();
        return DisplayTargets.Resolve(binding, DisplayBrightnessHub.MonitorNames, include);
    }

    private DisplayBrightnessSnapshot Current
    {
        get
        {
            var (binding, include) = BindingSnapshot();
            var monitors = DisplayBrightnessHub.MonitorNames;
            var target = DisplayTargets.Resolve(binding, monitors, include);
            var (index, count) = DisplayTargets.Position(target, monitors, include);

            if (target == DisplayTargets.StreamDeck)
            {
                return DisplayBrightnessRenderer.FromStreamDeck(BrightnessHub.Current, _deviceName, Math.Max(0, index - 1), count);
            }

            // Ask for the screen the binding resolved to, not the raw binding:
            // the resolution tolerates a respelled name, and the service's own
            // lookup does not, so the two could disagree about what is connected.
            var snapshot = DisplayBrightnessHub.Get(target ?? binding);
            if (!snapshot.Available)
            {
                return snapshot;
            }

            // The override is cosmetic and applied here, at the edge: everything
            // else keeps addressing the screen by the name the platform gave it.
            return snapshot with
            {
                Name = DisplayNames.Resolve(snapshot.Name),
                Index = Math.Max(0, index - 1),
                Count = count,
            };
        }
    }

    private async Task ApplyLayoutAndPushAsync()
    {
        try
        {
            await SendWatchdog.Watch(Connection.SetFeedbackLayoutAsync(LayoutPath), "layout");
        }
        catch (Exception ex)
        {
            Logger.Instance.LogMessage(TracingLevel.WARN, $"[action] display setFeedbackLayout failed: {ex.Message}");
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

        var delta = payload.Ticks * step;
        var target = ResolvedTarget();
        if (target == DisplayTargets.StreamDeck)
        {
            var state = BrightnessHub.Adjust(delta);
            _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.BrightnessKey, state.Level);
            return;
        }

        if (target is null || !DisplayBrightnessHub.Adjust(target, delta))
        {
            _ = SendWatchdog.Watch(Connection.ShowAlert(), "alert");
        }
    }

    protected override void OnDialDown(DialPayload payload) => Perform(Gesture(PressKey), "press");

    protected override void OnDialUp(DialPayload payload)
    {
    }

    protected override void OnTouchPress(TouchpadPressPayload payload) =>
        Perform(Gesture(payload.IsLongPress ? HoldKey : TapKey), payload.IsLongPress ? "strip hold" : "strip tap");

    private string Gesture(string key)
    {
        lock (_gate)
        {
            return key switch
            {
                PressKey => _press,
                TapKey => _tap,
                _ => _hold,
            };
        }
    }

    /// <param name="gesture">What to do.</param>
    /// <param name="source">What the user touched, for the log.</param>
    private void Perform(string gesture, string source)
    {
        switch (gesture)
        {
            case GestureDim:
                var current = ResolvedTarget();
                if (current == DisplayTargets.StreamDeck)
                {
                    var state = BrightnessHub.Toggle();
                    _ = GlobalSettingsStore.SaveAsync(Connection, GlobalSettingsStore.BrightnessKey, state.Level);
                }
                else if (current is null || !DisplayBrightnessHub.Toggle(current))
                {
                    _ = SendWatchdog.Watch(Connection.ShowAlert(), "alert");
                }

                break;

            case GestureNext:
            case GesturePrevious:
                var (binding, include) = BindingSnapshot();
                var from = DisplayTargets.Resolve(binding, DisplayBrightnessHub.MonitorNames, include) ?? binding;
                var target = DisplayTargets.Neighbor(binding, DisplayBrightnessHub.MonitorNames, include, gesture == GestureNext ? 1 : -1);
                if (target is null)
                {
                    _ = SendWatchdog.Watch(Connection.ShowAlert(), "alert"); // one target, or none: nothing to move to
                    return;
                }

                // Said in full, because a dial that "changed monitor by itself"
                // has so far always turned out to be a touch on the strip.
                Logger.Instance.LogMessage(TracingLevel.INFO,
                    $"[action] display dial {ShortContext}: {source} ({gesture}) moved it from {Describe(from)} to {Describe(target)}");
                BindTo(target);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The platform name a target refers to, or null when the target is not a
    /// monitor (the Stream Deck) or nothing is resolved. Custom names key off
    /// this, never off the binding, so "automatic" renames the screen it
    /// actually resolves to.
    /// </summary>
    private static string? ResolvedPlatformName(string? target) =>
        string.IsNullOrEmpty(target) || target == DisplayTargets.StreamDeck ? null : target;

    /// <summary>A target as the log should name it: the platform's name, plus the custom one when set.</summary>
    private static string Describe(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return "automatic";
        }

        var custom = DisplayNames.CustomFor(ResolvedPlatformName(target));
        return custom is null ? target : $"{target} (\"{custom}\")";
    }

    /// <summary>Enough of the context id to tell this deck's dials apart in the log.</summary>
    private string ShortContext => Connection.ContextId.Length > 6 ? Connection.ContextId[..6] : Connection.ContextId;

    /// <summary>Point this dial at a target and remember it in the action's settings.</summary>
    private void BindTo(string target)
    {
        JObject settings;
        lock (_gate)
        {
            // The inspector, if open, still shows the old screen's name until
            // the settings below reach it.
            _staleName = (SettingsReader.GetString(_settings, RenameKey, "").Trim(), DateTimeOffset.UtcNow + StaleNameWindow);
            _binding = target;
            _settings[MonitorKey] = target;
            // Keep the inspector's rename box pointed at the screen now shown,
            // so renaming never lands on the screen the dial just left.
            _settings[RenameKey] = DisplayNames.CustomFor(ResolvedPlatformName(target)) ?? "";
            settings = (JObject)_settings.DeepClone();
        }

        _ = SendWatchdog.Watch(Connection.SetSettingsAsync(settings), "settings");
        Push(full: false);
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
        var snapshot = Current;
        var isStreamDeck = ResolvedTarget() == DisplayTargets.StreamDeck;
        Dictionary<string, object> payload;
        lock (_gate)
        {
            var frame = DisplayBrightnessRenderer.Render(snapshot, TileFor(isStreamDeck, snapshot.Dimmed || !snapshot.Available));
            payload = FeedbackRenderer.Diff(full ? null : _lastFrame, frame);
            _lastFrame = frame;

            // The badge is this layout's own item, outside the shared frame.
            var badge = DisplayBrightnessRenderer.MonitorBadge(snapshot);
            if (full || badge != _lastBadge)
            {
                payload[DisplayBrightnessRenderer.BadgeKey] = badge;
                _lastBadge = badge;
            }
        }

        return payload;
    }

    /// <summary>Monitor or sun tile, lit or dimmed, rendered once each. Call under the gate.</summary>
    private string TileFor(bool streamDeck, bool dimmed)
    {
        var key = $"{(streamDeck ? "deck" : "monitor")}:{(dimmed ? "dim" : "on")}";
        if (!_tileCache.TryGetValue(key, out var dataUri))
        {
            var png = streamDeck ? ArtRenderer.RenderBrightnessTile(dimmed) : ArtRenderer.RenderMonitorTile(dimmed);
            dataUri = ArtRenderer.ToDataUri(png);
            _tileCache[key] = dataUri;
        }

        return dataUri;
    }

    // -- settings and lifetime ------------------------------------------------

    protected override void OnReceivedSettings(ReceivedSettingsPayload payload)
    {
        ApplySettings(payload.Settings, initial: false);
        Push(full: false);
    }

    protected override void OnReceivedGlobalSettings(ReceivedGlobalSettingsPayload payload)
    {
        GlobalSettingsStore.Apply(payload.Settings);

        // Even when the names did not change, this is the first moment the box
        // can be checked against them: the dial's settings arrive first.
        SyncNameBox();
        Push(full: false);
    }

    /// <param name="settings">The dial's settings.</param>
    /// <param name="initial">
    /// True for the settings the dial appeared with. Those are what was saved,
    /// never an edit, and they arrive before the global settings holding the
    /// names, so nothing in them is applied as a rename.
    /// </param>
    private void ApplySettings(JObject? settings, bool initial)
    {
        var renameTo = SettingsReader.GetString(settings, RenameKey, "").Trim();
        string previousBinding, binding;
        string? previousName;
        var stale = false;
        lock (_gate)
        {
            previousBinding = _binding;
            previousName = _settingsSeen ? SettingsReader.GetString(_settings, RenameKey, "") : null;
            _settingsSeen = true;

            if (_staleName is { } carried && DateTimeOffset.UtcNow < carried.Until)
            {
                stale = string.Equals(carried.Text, renameTo, StringComparison.Ordinal);
            }
            else
            {
                _staleName = null;
            }

            _settings = settings is null ? new JObject() : (JObject)settings.DeepClone();
            var monitor = SettingsReader.GetString(settings, MonitorKey, PropertyInspectorBridge.AutomaticValue);
            _binding = monitor == PropertyInspectorBridge.AutomaticValue ? DisplayTargets.Automatic : monitor;
            binding = _binding;
            // The setting is honoured only where the deck's screen can actually be
            // driven; elsewhere the deck never enters the cycle, so a profile
            // synced from Windows cannot offer a target that does nothing.
            _includeStreamDeck = BrightnessHub.IsSupported
                && SettingsReader.GetBool(settings, IncludeStreamDeckKey, fallback: true);
            _stepPercent = int.Parse(SettingsReader.GetString(settings, StepKey, "2", Steps));
            _press = SettingsReader.GetString(settings, PressKey, GestureDim, Gestures);
            _tap = SettingsReader.GetString(settings, TapKey, GestureNext, Gestures);
            _hold = SettingsReader.GetString(settings, HoldKey, GesturePrevious, Gestures);
        }

        if (!initial && !string.Equals(previousBinding, binding, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _staleName = (renameTo, DateTimeOffset.UtcNow + StaleNameWindow);
            }

            Logger.Instance.LogMessage(TracingLevel.INFO,
                $"[action] display dial {ShortContext}: inspector moved it from {Describe(previousBinding)} to {Describe(binding)}");
        }

        // A rename typed in the inspector applies to the screen this dial is
        // showing -- but only an actual edit of the box. Anything else that
        // carries the box along (the dial's own saved settings, a monitor
        // picked from the list, the echo of a save) leaves the names alone and
        // has the box corrected to the screen now shown.
        var renameTarget = ResolvedPlatformName(ResolvedTarget());
        if (!initial && !stale && renameTarget is not null && DisplayRename.IsEdit(previousBinding, previousName, binding, renameTo))
        {
            if (renameTo != (DisplayNames.CustomFor(renameTarget) ?? ""))
            {
                _ = DisplayNames.SetAsync(Connection, renameTarget, renameTo);
            }
        }
        else if (!initial)
        {
            SyncNameBox();
        }

        if (initial && SettingsReader.IsMissingAny(settings, MonitorKey, IncludeStreamDeckKey, StepKey, PressKey, TapKey, HoldKey))
        {
            JObject filled;
            lock (_gate)
            {
                _settings[MonitorKey] = _binding.Length == 0 ? PropertyInspectorBridge.AutomaticValue : _binding;
                _settings[IncludeStreamDeckKey] = _includeStreamDeck;
                _settings[StepKey] = _stepPercent.ToString();
                _settings[PressKey] = _press;
                _settings[TapKey] = _tap;
                _settings[HoldKey] = _hold;
                filled = (JObject)_settings.DeepClone();
            }

            _ = SendWatchdog.Watch(Connection.SetSettingsAsync(filled), "settings");
        }
    }

    /// <summary>
    /// Make the inspector's name box show the custom name of the screen this
    /// dial is on, so the next edit there lands on the screen the user is
    /// looking at. Left alone until the names have loaded, and while the dial
    /// is on nothing (its screen is missing), since there is no right answer
    /// to show then.
    /// </summary>
    private void SyncNameBox()
    {
        if (!GlobalSettingsStore.IsLoaded)
        {
            return;
        }

        var target = ResolvedTarget();
        if (target is null)
        {
            return;
        }

        var expected = DisplayNames.CustomFor(ResolvedPlatformName(target)) ?? "";
        JObject updated;
        lock (_gate)
        {
            // Compared trimmed: names are stored trimmed, and rewriting the box
            // mid-word would eat the space someone is in the middle of typing.
            if (!_settingsSeen || SettingsReader.GetString(_settings, RenameKey, "").Trim() == expected)
            {
                return;
            }

            _settings[RenameKey] = expected;
            updated = (JObject)_settings.DeepClone();
        }

        _ = SendWatchdog.Watch(Connection.SetSettingsAsync(updated), "settings");
    }

    protected override void OnDispose()
    {
        _pump.Stop();
        DisplayBrightnessHub.Changed -= _onMonitorChanged;
        DisplayBrightnessHub.MonitorsChanged -= _onMonitorsChanged;
        BrightnessHub.Changed -= _onStreamDeckChanged;
        DisplayNames.Changed -= _onNamesChanged;
        Logger.Instance.LogMessage(TracingLevel.INFO, "[action] display brightness disappear");
    }
}
