using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>Maps the monitor's brightness onto the shared six-item layout.</summary>
public static class DisplayBrightnessRenderer
{
    public static FeedbackFrame Render(DisplayBrightnessSnapshot snapshot, string iconValue)
    {
        if (!snapshot.Available)
        {
            return new FeedbackFrame(iconValue, snapshot.Name, "", false, 0, "", "");
        }

        return new FeedbackFrame(
            iconValue,
            snapshot.Dimmed ? $"Dimmed  {snapshot.Level}%" : $"Brightness  {snapshot.Level}%",
            MonitorLabel(snapshot),
            true,
            snapshot.Effective * FeedbackRenderer.BarRange / 100,
            "0",
            "100");
    }

    /// <summary>The monitor's name, with its position when more than one answers, so cycling is visible.</summary>
    public static string MonitorLabel(DisplayBrightnessSnapshot snapshot) =>
        snapshot.Count > 1 ? $"{snapshot.Name}  {snapshot.Index + 1}/{snapshot.Count}" : snapshot.Name;
}
