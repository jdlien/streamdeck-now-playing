using Newtonsoft.Json.Linq;

namespace NowPlaying.Plugin;

/// <summary>
/// Tolerant reads of action and global settings. The property inspector
/// stores booleans as JSON booleans and selects as strings, but a hand-edited
/// profile or an older version may hold anything, so every read has a
/// fallback and selects are checked against their allowed values.
/// </summary>
public static class SettingsReader
{
    public static string GetString(JObject? settings, string key, string fallback, params string[] allowed)
    {
        var value = settings?[key] is JValue { Type: JTokenType.String } token ? token.Value as string : null;
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        return allowed.Length == 0 || allowed.Contains(value) ? value : fallback;
    }

    public static bool GetBool(JObject? settings, string key, bool fallback) => settings?[key] switch
    {
        JValue { Type: JTokenType.Boolean } token => (bool)token.Value!,
        JValue { Type: JTokenType.String } token when bool.TryParse(token.Value as string, out var parsed) => parsed,
        _ => fallback,
    };

    /// <summary>True when <paramref name="settings"/> lacks any of <paramref name="keys"/>, meaning defaults should be written back so the inspector shows them.</summary>
    public static bool IsMissingAny(JObject? settings, params string[] keys) =>
        settings is null || keys.Any(key => settings[key] is null);
}
