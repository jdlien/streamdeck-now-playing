namespace NowPlaying.Device;

/// <summary>
/// Tidies the name a display reports for itself. EDID strings are whatever the
/// manufacturer typed: the 2022 Apple Studio Display calls itself
/// <c>StudioDisplay</c>, with no space, while the Studio Display XDR beside it
/// reports <c>Studio Display XDR</c>.
///
/// Deliberately a fixed list rather than a rule. Apple's product names are a
/// known, small set, so they can be corrected safely; guessing word boundaries
/// in an arbitrary manufacturer's string risks renaming someone's monitor to
/// something worse than it started. Anything not on the list is shown exactly
/// as the display reports it, and a user who dislikes it can set a custom name.
/// </summary>
public static class DisplayNameNormalizer
{
    /// <summary>
    /// Run-together names seen from Apple displays, and what to show instead.
    /// Compared ignoring case and spacing, so a future firmware that adds the
    /// space does not produce a double space.
    /// </summary>
    private static readonly (string Reported, string Display)[] KnownNames =
    [
        ("StudioDisplay", "Studio Display"),
        ("ProDisplayXDR", "Pro Display XDR"),
        ("StudioDisplayXDR", "Studio Display XDR"),
    ];

    /// <summary>The name to show for a display.</summary>
    public static string Normalize(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "";
        }

        foreach (var (reported, display) in KnownNames)
        {
            if (Squashed(trimmed).Equals(Squashed(reported), StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Makes every name in the list unique, since the name is the key the rest
    /// of the plugin binds dials and custom names to. Two Studio Displays of
    /// different generations both report "Studio Display", and without this
    /// they are one monitor as far as every dial is concerned.
    ///
    /// Only names that collide are touched, so a lone monitor keeps its plain
    /// name and existing bindings to it survive. A colliding name gets the last
    /// four digits of its EDID serial: stable across reboots and re-cabling,
    /// unlike a display id or a position. When that is not enough (no serial, or
    /// serials ending alike) a position number is added as a last resort.
    /// </summary>
    public static IReadOnlyList<string> Disambiguate(IReadOnlyList<(string Name, uint Serial)> displays)
    {
        var result = displays.Select(d => d.Name).ToArray();
        foreach (var group in displays.Select((d, i) => (d, i)).GroupBy(x => Squashed(x.d.Name), StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            foreach (var (d, i) in members)
            {
                result[i] = d.Serial == 0 ? d.Name : $"{d.Name} ({d.Serial % 10000:D4})";
            }
        }

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < result.Length; i++)
        {
            seen[result[i]] = seen.GetValueOrDefault(result[i]) + 1;
            if (seen[result[i]] > 1)
            {
                result[i] = $"{result[i]} #{seen[result[i]]}";
            }
        }

        return result;
    }

    private static string Squashed(string value) => value.Replace(" ", "");
}
