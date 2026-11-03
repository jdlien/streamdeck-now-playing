using BarRaider.SdTools;
using Newtonsoft.Json.Linq;

namespace NowPlaying.Plugin;

/// <summary>
/// The plugin's global settings, as last received, so a single key can be
/// updated without clobbering the others. Every action feeds
/// <see cref="Apply"/> from its ReceivedGlobalSettings.
///
/// Nothing is written until the app has sent the settings at least once. The
/// store starts empty, and a save merged into that empty object used to go out
/// as the whole of the plugin's global settings: one early write -- a custom
/// name applied while the actions were still appearing -- replaced every other
/// screen's name, the preferred player and the saved brightness. A change made
/// before then is held and replayed on top of what the app sends.
///
/// Recent changes are also replayed onto every copy that arrives for a short
/// while after them. Every action asks for the global settings as it appears,
/// and StreamDeck-Tools hands each answer to every action, so one startup sees
/// dozens of copies -- and an answer to a request sent before a change still
/// carries the state from before it. Taking such a copy at face value undid
/// the change locally, and the next save of any key wrote the undo back.
/// </summary>
internal static class GlobalSettingsStore
{
    private static readonly object Gate = new();
    private static readonly List<Action<JObject>> Pending = [];
    private static readonly List<(Action<JObject> Change, DateTimeOffset At)> Recent = [];
    private static JObject _latest = new();
    private static bool _loaded;
    private static ISDConnection? _pendingConnection;

    /// <summary>
    /// How long a change is replayed onto arriving copies. Long enough to
    /// outlast the answers to every request already on the wire when it was
    /// made, which come back within milliseconds; short enough that the list
    /// stays a handful of entries.
    /// </summary>
    internal static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(10);

    public const string BrightnessKey = "brightness";

    /// <summary>True once the app has sent the global settings.</summary>
    public static bool IsLoaded
    {
        get
        {
            lock (Gate)
            {
                return _loaded;
            }
        }
    }

    /// <summary>Record what the app sent and fan it out to the hubs that care.</summary>
    public static void Apply(JObject? settings)
    {
        JObject applied;
        JObject? flush = null;
        ISDConnection? flushTo = null;
        lock (Gate)
        {
            _latest = settings is null ? new JObject() : (JObject)settings.DeepClone();
            _loaded = true;

            var now = DateTimeOffset.UtcNow;
            Recent.RemoveAll(r => now - r.At > ReplayWindow);
            foreach (var (change, _) in Recent)
            {
                change(_latest);
            }

            if (Pending.Count > 0)
            {
                foreach (var change in Pending)
                {
                    change(_latest);
                    Recent.Add((change, now));
                }

                Pending.Clear();
                flush = (JObject)_latest.DeepClone();
                flushTo = _pendingConnection;
                _pendingConnection = null;
            }

            applied = (JObject)_latest.DeepClone();
        }

        if (flush is not null && flushTo is not null)
        {
            _ = SendWatchdog.Watch(flushTo.SetGlobalSettingsAsync(flush, false), "global settings");
        }

        DisplayNames.Apply(applied);
        MediaHub.SetPreferredAppId(PropertyInspectorBridge.PreferredAppFrom(applied));
        BrightnessHub.LoadSavedLevel(applied[BrightnessKey]?.Type == JTokenType.Integer ? (int?)applied[BrightnessKey]!.Value<int>() : null);
    }

    /// <summary>Write one key back, merged into the latest copy, without triggering didReceiveGlobalSettings.</summary>
    public static Task SaveAsync(ISDConnection connection, string key, JToken value)
    {
        var copy = value.DeepClone();
        return UpdateAsync(connection, settings => settings[key] = copy.DeepClone());
    }

    /// <summary>
    /// Change the global settings in place and write them back. The change is
    /// a function rather than a value so that, held until the first load, it
    /// can be replayed onto what the app actually has instead of overwriting it.
    /// </summary>
    public static Task UpdateAsync(ISDConnection connection, Action<JObject> change)
    {
        JObject merged;
        lock (Gate)
        {
            change(_latest);
            if (!_loaded)
            {
                Pending.Add(change);
                _pendingConnection = connection;
                return Task.CompletedTask;
            }

            var now = DateTimeOffset.UtcNow;
            Recent.RemoveAll(r => now - r.At > ReplayWindow);
            Recent.Add((change, now));
            merged = (JObject)_latest.DeepClone();
        }

        return SendWatchdog.Watch(connection.SetGlobalSettingsAsync(merged, false), "global settings");
    }

    /// <summary>Back to the state at process start. For tests: the store is process-wide.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _latest = new JObject();
            _loaded = false;
            _pendingConnection = null;
            Pending.Clear();
            Recent.Clear();
        }
    }

    /// <summary>A copy of one key as last received or changed, or null.</summary>
    public static JToken? Get(string key)
    {
        lock (Gate)
        {
            return _latest[key]?.DeepClone();
        }
    }
}
