using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// Maps a monitor's brightness onto the shared six-item layout, plus the
/// layout's seventh item: a small right-aligned "1/2" badge at the end of
/// the name row when more than one monitor answers.
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

    /// <summary>"1/2" when there is a choice of monitors; empty otherwise, so the badge item disappears.</summary>
    public static string MonitorBadge(DisplayBrightnessSnapshot snapshot) =>
        snapshot.Available && snapshot.Count > 1 ? $"{snapshot.Index + 1}/{snapshot.Count}" : "";
}
