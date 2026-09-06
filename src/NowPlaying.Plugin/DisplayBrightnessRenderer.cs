using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// Maps a target's brightness onto the shared six-item layout, plus the
/// layout's seventh item: a small right-aligned "1/2" badge at the end of
/// the name row when there is more than one target to cycle through.
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
