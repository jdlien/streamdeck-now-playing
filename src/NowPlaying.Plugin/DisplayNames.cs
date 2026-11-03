using BarRaider.SdTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NowPlaying.Plugin;

/// <summary>
/// User-chosen names for screens, kept in global settings so every dial agrees
/// and a name survives the dial cycling away and back.
///
/// Keyed by the name the platform reports, not by the dial: a display's own
/// name is long and often unhelpful ("Studio Display XDR" does not say which
/// desk it is on, and a 2022 and a 2026 Studio Display report the same string), while the
/// strip has room for about sixteen characters. "Left" and "Centre" fit.
///
/// The stored key stays the platform's name, so the override is cosmetic and
/// nothing else in the system has to know about it. Panels that share a name are told
/// apart by the EDID serial the platform layer appends to their names, so each
/// gets a custom name of its own.
/// </summary>
internal static class DisplayNames
{
    /// <summary>Global setting key holding the map of platform name to custom name.</summary>
    public const string GlobalKey = "monitorNames";

    private static readonly object Gate = new();
    private static JObject _names = new();

    /// <summary>
    /// The map changed: a rename here, or a different map from the app. Dials
    /// showing a renamed screen redraw their label and refresh the name box.
    /// </summary>
    public static event Action? Changed;

    /// <summary>Take the map from the global settings the app just sent.</summary>
    public static void Apply(JObject? globalSettings)
    {
        var map = globalSettings?[GlobalKey] as JObject;
        bool changed;
        lock (Gate)
        {
            var next = map is null ? new JObject() : (JObject)map.DeepClone();
            changed = !JToken.DeepEquals(_names, next);
            _names = next;
        }

        if (changed)
        {
            Logger.Instance.LogMessage(TracingLevel.INFO, $"[display] custom names: {Describe()}");
            Guard.Raise(Changed, "DisplayNames.Changed");
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
        var trimmed = customName?.Trim() ?? "";
        lock (Gate)
        {
            Set(_names, platformName, trimmed);
        }

        Logger.Instance.LogMessage(TracingLevel.INFO, trimmed.Length == 0
            ? $"[display] custom name cleared for {platformName}"
            : $"[display] {platformName} renamed to \"{trimmed}\"");
        Guard.Raise(Changed, "DisplayNames.Changed");

        // Changed on the store's own copy of the map, never by sending this
        // class's copy back: that copy can predate the app's, and writing it
        // whole is how the other screens' names used to disappear.
        return GlobalSettingsStore.UpdateAsync(connection, settings =>
        {
            if (settings[GlobalKey] is not JObject map)
            {
                map = new JObject();
                settings[GlobalKey] = map;
            }

            Set(map, platformName, trimmed);
        });
    }

    private static void Set(JObject map, string platformName, string customName)
    {
        if (customName.Length == 0)
        {
            map.Remove(platformName);
        }
        else
        {
            map[platformName] = customName;
        }
    }

    private static string Describe()
    {
        lock (Gate)
        {
            return _names.Count == 0 ? "(none)" : _names.ToString(Formatting.None);
        }
    }
}
