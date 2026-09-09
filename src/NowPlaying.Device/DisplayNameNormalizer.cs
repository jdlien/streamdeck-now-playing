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

    private static string Squashed(string value) => value.Replace(" ", "");
}
