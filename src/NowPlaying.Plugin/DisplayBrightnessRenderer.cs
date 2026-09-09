using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// Maps a target's brightness onto the shared six-item layout, plus the
/// layout's seventh item: a small right-aligned "1/2" badge showing which
/// screen of how many this dial is on, when there is more than one to cycle
/// through.
///
/// The badge sits at the right of the value row rather than the name row.
/// Screen names are long -- "Studio Display XDR" measures 146px at the layout's
/// font, against 135px of name row once the icon and a badge are taken out --
/// so the name gets the whole top row and the badge takes some of the 53px the
/// value row has spare.
/// </summary>
public static class DisplayBrightnessRenderer
{
    public const string BadgeKey = "badge";

    public static FeedbackFrame Render(DisplayBrightnessSnapshot snapshot, string iconValue)
    {
        if (!snapshot.Available)
        {
            return new FeedbackFrame(iconValue, snapshot.Name, "", false, 0, "", "");
        }

        return new FeedbackFrame(
            iconValue,
            snapshot.Dimmed ? $"Dimmed  {snapshot.Level}%" : $"Brightness  {snapshot.Level}%",
            snapshot.Name,
            true,
            snapshot.Effective * FeedbackRenderer.BarRange / 100,
            "0",
            "100");
    }

    /// <summary>"1/2" when there is a choice of targets; empty otherwise, so the badge item disappears.</summary>
    public static string MonitorBadge(DisplayBrightnessSnapshot snapshot) =>
        snapshot.Available && snapshot.Count > 1 ? $"{snapshot.Index + 1}/{snapshot.Count}" : "";

    /// <summary>The Stream Deck's own brightness in the same shape as a monitor's, so one renderer serves both.</summary>
    public static DisplayBrightnessSnapshot FromStreamDeck(BrightnessState state, string deviceName, int index, int count) =>
        new(deviceName, state.Level, state.Dimmed, true, index, count);
}
