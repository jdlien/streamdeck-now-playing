using Newtonsoft.Json.Linq;

namespace NowPlaying.Plugin;

/// <summary>
/// Builds the setFeedback payload as a JObject without reflection.
/// JObject.FromObject reflects over the value's type, which a trimmed
/// publish can strip (IL2026 in the trimmed build); the payload is only
/// strings, numbers, booleans, and nested maps, so it is built by hand.
/// </summary>
public static class FeedbackJson
{
    public static JObject ToJObject(IReadOnlyDictionary<string, object> payload)
    {
        var result = new JObject();
        foreach (var (key, value) in payload)
        {
            result[key] = ToToken(value);
        }

        return result;
    }

    private static JToken ToToken(object value) => value switch
    {
        string s => new JValue(s),
        bool b => new JValue(b),
        int i => new JValue(i),
        long l => new JValue(l),
        double d => new JValue(d),
        IReadOnlyDictionary<string, object> nested => ToJObject(nested),
        _ => throw new NotSupportedException($"Feedback payloads cannot carry a {value.GetType().Name}."),
    };
}
