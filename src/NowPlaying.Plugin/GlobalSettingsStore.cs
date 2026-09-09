using BarRaider.SdTools;
using Newtonsoft.Json.Linq;

namespace NowPlaying.Plugin;

/// <summary>
/// The plugin's global settings, as last received, so a single key can be
/// updated without clobbering the others. Every action feeds
/// <see cref="Apply"/> from its ReceivedGlobalSettings.
/// </summary>
internal static class GlobalSettingsStore
{
    private static readonly object Gate = new();
    private static JObject _latest = new();

    public const string BrightnessKey = "brightness";

    /// <summary>Record what the app sent and fan it out to the hubs that care.</summary>
    public static void Apply(JObject? settings)
    {
        lock (Gate)
        {
            _latest = settings is null ? new JObject() : (JObject)settings.DeepClone();
        }

        DisplayNames.Apply(settings);
        MediaHub.SetPreferredAppId(PropertyInspectorBridge.PreferredAppFrom(settings));
        BrightnessHub.LoadSavedLevel(settings?[BrightnessKey]?.Type == JTokenType.Integer ? (int?)settings[BrightnessKey]!.Value<int>() : null);
    }

    /// <summary>Write one key back, merged into the latest copy, without triggering didReceiveGlobalSettings.</summary>
    public static Task SaveAsync(ISDConnection connection, string key, JToken value)
    {
        JObject merged;
        lock (Gate)
        {
            _latest[key] = value;
            merged = (JObject)_latest.DeepClone();
        }

        return connection.SetGlobalSettingsAsync(merged, false);
    }
}
