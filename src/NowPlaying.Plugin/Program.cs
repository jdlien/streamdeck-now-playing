using BarRaider.SdTools;

namespace NowPlaying.Plugin;

internal static class Program
{
    /// <summary>
    /// The Stream Deck app launches this exe with -port, -pluginUUID,
    /// -registerEvent and -info; StreamDeck-Tools parses them, connects, and
    /// dispatches events to the classes marked with PluginActionId.
    /// </summary>
    private static void Main(string[] args)
    {
        SDWrapper.Run(args);
    }
}
