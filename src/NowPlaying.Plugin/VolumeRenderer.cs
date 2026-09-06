using NowPlaying.Audio;

namespace NowPlaying.Plugin;

/// <summary>
/// Maps the volume state onto the same six layout items the Now Playing
/// dial uses, so the two dials read as one design: speaker tile, device
/// name on the top row, "Volume 42%" bold on the full-width row, the bar as
/// the level, and the scale's ends as the small labels.
/// </summary>
public static class VolumeRenderer
{
    public const string NoDeviceText = "No audio device";

    public static FeedbackFrame Render(VolumeSnapshot volume, string iconValue)
    {
        if (!volume.HasDevice)
        {
            return new FeedbackFrame(iconValue, NoDeviceText, "", false, 0, "", "");
        }

        var percent = volume.Percent;
        return new FeedbackFrame(
            iconValue,
            volume.Muted ? $"Muted  {percent}%" : $"Volume  {percent}%",
            volume.DeviceName,
            true,
            percent * FeedbackRenderer.BarRange / 100,
            "0",
            "100");
    }
}
