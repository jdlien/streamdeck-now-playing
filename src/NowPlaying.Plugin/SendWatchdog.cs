using BarRaider.SdTools;

namespace NowPlaying.Plugin;

/// <summary>
/// The plugin's last resort against a connection to the Stream Deck app that
/// has stopped taking messages.
///
/// StreamDeck-Tools sends everything -- every dial's feedback, every key image,
/// every layout and settings write -- one at a time through one socket, with
/// no deadline. A send that never returns therefore silences the whole plugin
/// for good, and nothing short of a new connection recovers it. The only way
/// to get one is to exit and let the app relaunch the plugin, which it does
/// within a couple of seconds. Every send goes through <see cref="Watch"/>, so
/// whichever one is first in line when the socket stalls is the one that
/// notices -- not only those of a dial that happens to be redrawing.
/// </summary>
internal static class SendWatchdog
{
    /// <summary>
    /// How long a send may stay outstanding. A message to an app on the same
    /// machine never legitimately takes this long; the process is suspended
    /// across sleep, so a sleep does not count against it.
    /// </summary>
    internal static readonly TimeSpan HardLimit = TimeSpan.FromSeconds(90);

    private static int _exiting;

    /// <summary>Arm the deadline for one send, and hand the send back.</summary>
    /// <param name="send">The send.</param>
    /// <param name="what">What it is, for the log.</param>
    public static Task Watch(Task send, string what)
    {
        if (send.IsCompleted)
        {
            return send;
        }

        _ = send.WaitAsync(HardLimit).ContinueWith(waited =>
        {
            if (waited.Exception?.InnerException is TimeoutException)
            {
                ExitForANewConnection(what);
            }
        }, TaskScheduler.Default);

        return send;
    }

    private static void ExitForANewConnection(string what)
    {
        if (Interlocked.Exchange(ref _exiting, 1) != 0)
        {
            return;
        }

        Logger.Instance.LogMessage(TracingLevel.FATAL,
            $"[connection] a {what} send to the Stream Deck app has been outstanding for {HardLimit.TotalSeconds:0}s; " +
            "the connection has stopped taking messages, so exiting for the app to relaunch the plugin");

        // Exit runs shutdown handlers, and one that blocks would leave the
        // process up and silent, which is the state being escaped.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Environment.FailFast("exit did not complete after a stalled Stream Deck connection");
        });
        Environment.Exit(4);
    }
}
