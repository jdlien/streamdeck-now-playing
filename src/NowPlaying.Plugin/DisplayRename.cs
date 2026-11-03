namespace NowPlaying.Plugin;

/// <summary>
/// When a display dial's settings carry a rename, and when they merely carry
/// the name box along. Pure, because the distinction is what keeps one
/// screen's name from landing on another.
///
/// The name box is per dial, but a custom name belongs to a screen. The box
/// therefore mirrors whichever screen the dial shows, and sdpi sends the whole
/// settings object whenever any one control changes. Picking another monitor
/// in the inspector arrives as "monitor: XDR, renameTo: (the previous
/// screen's name)", and treating that as a rename gave the XDR the right-hand
/// panel's name: the dial then read "Studio Display 2022" while turning the
/// XDR, which looked exactly like the dial had jumped to the other monitor.
/// </summary>
public static class DisplayRename
{
    /// <summary>
    /// True only when the user typed in the name box: the dial's binding is
    /// the same before and after, and the box's text is not.
    /// </summary>
    /// <param name="previousBinding">The binding before this update.</param>
    /// <param name="previousName">
    /// The box's text before this update, or null when this dial has not seen
    /// its settings yet. The first settings a dial sees are never a rename:
    /// they are what was saved, and the names they were saved against may not
    /// even have loaded yet.
    /// </param>
    /// <param name="binding">The binding in this update.</param>
    /// <param name="name">The box's text in this update.</param>
    public static bool IsEdit(string previousBinding, string? previousName, string binding, string name) =>
        previousName is not null
        && string.Equals(previousBinding, binding, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(previousName.Trim(), name.Trim(), StringComparison.Ordinal);
}
