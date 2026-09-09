using BarRaider.SdTools;
using Newtonsoft.Json.Linq;

namespace NowPlaying.Plugin;

/// <summary>
/// User-chosen names for screens, kept in global settings so every dial agrees
/// and a name survives the dial cycling away and back.
///
/// Keyed by the name the platform reports, not by the dial: a display's own
/// name is long and often unhelpful ("Studio Display XDR" does not say which
/// desk it is on, and two identical panels report the same string), while the
/// strip has room for about sixteen characters. "Left" and "Centre" fit.
///
/// The stored key stays the platform's name, so the override is cosmetic and
/// nothing else in the system has to know about it. Two identical monitors
/// reporting the same name therefore share one custom name; distinguishing
/// them needs the stable per-display identity in macos-port-plan D8, which is
/// not built yet.
/// </summary>
internal static class DisplayNames
{
    /// <summary>Global setting key holding the map of platform name to custom name.</summary>
    public const string GlobalKey = "monitorNames";

    private static readonly object Gate = new();
    private static JObject _names = new();

    /// <summary>Take the map from the global settings the app just sent.</summary>
    public static void Apply(JObject? globalSettings)
    {
        var map = globalSettings?[GlobalKey] as JObject;
        lock (Gate)
        {
            _names = map is null ? new JObject() : (JObject)map.DeepClone();
        }
    }

    /// <summary>The custom name for a screen, or null when it has none.</summary>
    public static string? CustomFor(string? platformName)
    {
        if (string.IsNullOrEmpty(platformName))
        {
            return null;
        }

        lock (Gate)
        {
            var value = _names[platformName]?.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>What to show for a screen: its custom name when set, otherwise the platform's.</summary>
    public static string Resolve(string? platformName) => CustomFor(platformName) ?? platformName ?? "";

    /// <summary>Set or clear a screen's custom name. An empty name removes the override.</summary>
    public static Task SetAsync(ISDConnection connection, string platformName, string? customName)
    {
        JObject map;
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(customName))
            {
                _names.Remove(platformName);
            }
            else
            {
                _names[platformName] = customName.Trim();
            }

            map = (JObject)_names.DeepClone();
        }

        return GlobalSettingsStore.SaveAsync(connection, GlobalKey, map);
    }
}
