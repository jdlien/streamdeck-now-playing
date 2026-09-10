using System.Diagnostics;

namespace NowPlaying.Media;

/// <summary>
/// Why a read produced no state. The distinction matters: "Music says it is
/// stopped" means hand the session back, while "Music did not answer" must not,
/// because dropping the route on a slow query blanks the display and there may
/// be nothing left to restore it -- if the helper is the reason this adapter is
/// being used, it will not be volunteering a snapshot either.
/// </summary>
internal enum MusicReadStatus
{
    /// <summary>A track is loaded and <see cref="MusicReadOutcome.State"/> describes it.</summary>
    Ok,

    /// <summary>Music is not running, or is stopped with no track. Genuinely nothing to show.</summary>
    NotPlaying,

    /// <summary>The query timed out or errored. Says nothing about what Music is doing.</summary>
    Failed,
}

/// <summary>The result of asking Music what it is doing.</summary>
internal readonly record struct MusicReadOutcome(MusicReadStatus Status, MusicState? State)
{
    public static MusicReadOutcome Ok(MusicState state) => new(MusicReadStatus.Ok, state);

    public static readonly MusicReadOutcome NotPlaying = new(MusicReadStatus.NotPlaying, null);

    public static readonly MusicReadOutcome Failed = new(MusicReadStatus.Failed, null);
}

/// <summary>What Music.app reports about itself.</summary>
internal sealed record MusicState(
    PlaybackState State,
    string Title,
    string Artist,
    string Album,
    TimeSpan? Position,
    TimeSpan? Duration,
    string TrackId);

/// <summary>
/// Music.app over AppleScript.
///
/// The fallback for when MediaRemote names Music as the owning app but will not
/// describe what it is playing, and for when the helper is not answering at
/// all. It was written because that first case looked permanent: the metadata
/// call was measured hanging out to thirty seconds. The cause was a deadlock in
/// the helper, not in Music, and MediaRemote now answers for it -- but Music's
/// scripting dictionary has everything the snapshot needs, and it is the one
/// source here that does not depend on a private framework, so it stays.
///
/// Two rules everything here follows. Queries are bounded, because an
/// unresponsive app would otherwise hang whatever called it. And nothing may
/// launch Music: every script is guarded on it already running, so a dial does
/// not open a media player just by being on a page.
/// </summary>
internal sealed class MusicAppAdapter(Action<string>? log = null)
{
    private const string Bundle = "com.apple.Music";

    /// <summary>The bundle id this adapter answers for.</summary>
    public static string BundleId => Bundle;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Artwork moves far more data than a state read -- 935 KB for one track on
    /// the development machine -- so it gets its own, longer bound. Sharing the
    /// state timeout made artwork reads fail routinely.
    /// </summary>
    private static readonly TimeSpan ArtworkTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Unit separator: safe in a field value, unlike anything typographic.</summary>
    private const string Separator = "";

    private static readonly string StateScript = $$"""
        if application "Music" is not running then return "notrunning"
        tell application "Music"
            set pState to (player state as text)
            if pState is "stopped" then return "stopped"
            try
                set t to current track
            on error
                return "stopped"
            end try
            set out to pState & "{{Separator}}" & (get name of t) & "{{Separator}}" & (get artist of t)
            set out to out & "{{Separator}}" & (get album of t) & "{{Separator}}" & (player position as text)
            set out to out & "{{Separator}}" & ((duration of t) as text) & "{{Separator}}" & (get persistent ID of t)
            return out
        end tell
        """;

    /// <summary>What Music is doing, distinguishing "nothing on" from "did not answer".</summary>
    public async Task<MusicReadOutcome> ReadAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunAsync(StateScript, cancellationToken).ConfigureAwait(false);
        if (output is null)
        {
            return MusicReadOutcome.Failed;   // timeout or error: tells us nothing
        }

        if (output is "notrunning" or "stopped")
        {
            return MusicReadOutcome.NotPlaying;
        }

        var parts = output.Split(Separator);
        if (parts.Length < 7)
        {
            log?.Invoke($"unexpected reply from Music: {parts.Length} fields");
            return MusicReadOutcome.Failed;
        }

        var state = parts[0] switch
        {
            "playing" => PlaybackState.Playing,
            "paused" => PlaybackState.Paused,
            _ => PlaybackState.Stopped,
        };

        return MusicReadOutcome.Ok(new MusicState(
            state,
            parts[1].Trim(),
            parts[2].Trim(),
            parts[3].Trim(),
            ParseSeconds(parts[4]),
            ParseSeconds(parts[5]),
            parts[6].Trim()));
    }

    /// <summary>Artwork for the current track, or null when there is none.</summary>
    public async Task<byte[]?> ReadArtworkAsync(CancellationToken cancellationToken = default)
    {
        // The bytes come back through a temp file: artwork is binary and
        // osascript's text output would mangle it.
        var path = Path.Combine(Path.GetTempPath(), $"nowplaying-art-{Guid.NewGuid():N}");
        var script = $$"""
            if application "Music" is not running then return "no"
            tell application "Music"
                if player state is stopped then return "no"
                try
                    set art to data of artwork 1 of current track
                on error
                    return "no"
                end try
            end tell
            set f to open for access POSIX file "{{path}}" with write permission
            try
                set eof f to 0
                write art to f
                close access f
            on error
                try
                    close access f
                end try
                return "no"
            end try
            return "ok"
            """;

        try
        {
            var result = await RunAsync(script, cancellationToken, ArtworkTimeout).ConfigureAwait(false);
            if (result != "ok" || !File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            // AppleScript writes the raw picture data with a small header of its
            // own on some macOS versions; the decoder tolerates a leading offset,
            // so the bytes are handed over as-is and validated by whoever decodes.
            return bytes.Length > 0 ? bytes : null;
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Check once, loudly, whether Apple events are allowed.
    ///
    /// Worth doing because a TCC denial is indistinguishable from "nothing is
    /// playing": every query just returns nothing and the dial sits empty
    /// forever with no clue why. The same trap is written up in
    /// ../ak820-pro/hostagent/nowplaying-macos.sh, which learned it the hard way.
    ///
    /// The prompt is attributed to the Stream Deck app, since that is the
    /// process at the top of the chain, and a background-launched process often
    /// cannot raise it at all -- hence the instruction in the log.
    /// </summary>
    public async Task<bool> ProbeAutomationAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("return (application \"Music\" is running) as text", cancellationToken)
            .ConfigureAwait(false);
        if (result is not null)
        {
            return true;
        }

        log?.Invoke("Music automation is unavailable. If this is a permission problem, allow " +
                    "\"Elgato Stream Deck\" to control \"Music\" in System Settings > Privacy & " +
                    "Security > Automation. The prompt may never appear on its own for a " +
                    "background-launched plugin; opening Music and using the dial once usually raises it.");
        return false;
    }

    public Task<bool> ToggleAsync() => CommandAsync("playpause");

    public Task<bool> NextAsync() => CommandAsync("next track");

    public Task<bool> PreviousAsync() => CommandAsync("previous track");

    private async Task<bool> CommandAsync(string verb)
    {
        var script = $"""
            if application "Music" is not running then return "no"
            tell application "Music" to {verb}
            return "ok"
            """;
        return await RunAsync(script).ConfigureAwait(false) == "ok";
    }

    private async Task<string?> RunAsync(string script, CancellationToken cancellationToken = default, TimeSpan? bound = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(bound ?? QueryTimeout);

        var startInfo = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = (await stderr.ConfigureAwait(false)).Trim();
                // -1743 is "not authorised to send Apple events", the TCC denial.
                if (error.Contains("-1743", StringComparison.Ordinal))
                {
                    log?.Invoke("Music is not authorised: allow the Stream Deck app to control Music in " +
                                "System Settings > Privacy & Security > Automation");
                }
                else if (error.Length > 0)
                {
                    log?.Invoke($"Music query failed: {error}");
                }

                return null;
            }

            return (await stdout.ConfigureAwait(false)).Trim();
        }
        catch (OperationCanceledException)
        {
            log?.Invoke("Music did not answer in time");
            return null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Music query error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Seconds, as Music reports them.
    ///
    /// If another player is ever added here, check its units first: Spotify
    /// reports track duration in MILLISECONDS while Music uses seconds, so
    /// sharing this parser between them shows a three-minute track as three
    /// seconds and looks exactly like a rendering bug. Recorded in
    /// ../ak820-pro/hostagent/nowplaying-macos.sh, which hit it.
    /// </summary>
    private static TimeSpan? ParseSeconds(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
