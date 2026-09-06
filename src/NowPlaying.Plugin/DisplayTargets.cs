namespace NowPlaying.Plugin;

/// <summary>
/// The list a display brightness dial can point at: the monitors that answer
/// DDC/CI, primary first, and optionally the Stream Deck itself as the last
/// entry. Pure, so the ordering and wrapping rules are unit tested.
/// </summary>
public static class DisplayTargets
{
    /// <summary>The binding value that means the Stream Deck's own screen.</summary>
    public const string StreamDeck = "streamdeck";

    /// <summary>The binding value that means "automatic": the primary monitor, or the Stream Deck when no monitor answers and it is included.</summary>
    public const string Automatic = "";

    public static IReadOnlyList<string> Order(IReadOnlyList<string> monitors, bool includeStreamDeck) =>
        includeStreamDeck ? [.. monitors, StreamDeck] : monitors;

    /// <summary>The concrete target a binding refers to right now, or null when it is not available. An explicit Stream Deck binding is always honoured.</summary>
    public static string? Resolve(string binding, IReadOnlyList<string> monitors, bool includeStreamDeck)
    {
        if (binding == StreamDeck)
        {
            return StreamDeck;
        }

        if (binding.Length == 0)
        {
            return monitors.Count > 0 ? monitors[0] : includeStreamDeck ? StreamDeck : null;
        }

        return monitors.FirstOrDefault(m => string.Equals(m, binding, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The target after (+1) or before (-1) the binding in the list, wrapping. Null when there is nothing to move to.</summary>
    public static string? Neighbor(string binding, IReadOnlyList<string> monitors, bool includeStreamDeck, int direction)
    {
        var list = Order(monitors, includeStreamDeck);
        if (list.Count < 2)
        {
            return null;
        }

        var current = Resolve(binding, monitors, includeStreamDeck);
        var index = current is null ? -1 : IndexOf(list, current);
        if (index < 0)
        {
            // Bound to something outside the cycle (the deck while excluded, or a missing monitor): enter at an end.
            return direction > 0 ? list[0] : list[^1];
        }

        var count = list.Count;
        return list[((index + direction) % count + count) % count];
    }

    /// <summary>1-based position and count for the badge; (0, 0) when the target is outside the list.</summary>
    public static (int Index, int Count) Position(string? target, IReadOnlyList<string> monitors, bool includeStreamDeck)
    {
        if (target is null)
        {
            return (0, 0);
        }

        var list = Order(monitors, includeStreamDeck);
        var index = IndexOf(list, target);
        return index < 0 ? (0, 0) : (index + 1, list.Count);
    }

    private static int IndexOf(IReadOnlyList<string> list, string target)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], target, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
