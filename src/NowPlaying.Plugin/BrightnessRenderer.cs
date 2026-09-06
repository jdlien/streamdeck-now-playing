using NowPlaying.Device;

namespace NowPlaying.Plugin;

/// <summary>
/// Maps the brightness state onto the shared six-item layout: a sun tile,
/// the device model on the top row, "Brightness 60%" on the full-width row,
/// the bar as the level, and 0 and 100 as the scale's ends.
/// </summary>
public static class BrightnessRenderer
{
    public static FeedbackFrame Render(BrightnessState state, string deviceName, string iconValue) => new(
        iconValue,
        state.Dimmed ? $"Dimmed  {state.Level}%" : $"Brightness  {state.Level}%",
        deviceName,
        true,
        state.Effective * FeedbackRenderer.BarRange / 100,
        "0",
        "100");

    /// <summary>A readable model name from StreamDeck-Tools' device type enum name.</summary>
    public static string DeviceName(string? deviceType) => deviceType switch
    {
        "StreamDeckPlus" => "Stream Deck +",
        "StreamDeckXL" => "Stream Deck XL",
        "StreamDeckMini" => "Stream Deck Mini",
        "StreamDeckMobile" => "Stream Deck Mobile",
        "StreamDeckPedal" => "Stream Deck Pedal",
        "StreamDeckNeo" => "Stream Deck Neo",
        "StreamDeckClassic" or "StreamDeck" => "Stream Deck",
        null or "" => "Stream Deck",
        _ => deviceType.Replace("StreamDeck", "Stream Deck "),
    };
}
