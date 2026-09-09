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

    /// <summary>Shown when the device plays audio but has no software volume, so the dial cannot do anything.</summary>
    public const string HardwareOnlyText = "Hardware volume";

    public static FeedbackFrame Render(VolumeSnapshot volume, string iconValue)
    {
        if (!volume.HasDevice)
        {
            return new FeedbackFrame(iconValue, NoDeviceText, "", false, 0, "", "");
        }

        // A device with a physical gain knob and no software volume: name it,
        // say why the dial is inert, and hide the bar rather than showing a
        // level that cannot move.
        if (!volume.CanSetVolume)
        {
            return new FeedbackFrame(iconValue, HardwareOnlyText, volume.DeviceName, false, 0, "", "");
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
