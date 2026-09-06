using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>What the layout items should show. Compared by value to skip redundant sends.</summary>
/// <param name="Icon">Path of the icon image relative to the plugin folder, or null to hide it.</param>
/// <param name="Elapsed">Elapsed time label, empty when there is no usable duration.</param>
/// <param name="Total">Track length label, empty when there is no usable duration.</param>
public sealed record FeedbackFrame(
    string? Icon,
    string Track,
    string Artist,
    bool BarEnabled,
    int BarValue,
    string Elapsed,
    string Total);

/// <summary>
/// Pure mapping from a snapshot plus the wall clock to the layout's items
/// (README section 6), and the diff that keeps setFeedback payloads minimal.
/// No Stream Deck types here so it can be unit tested.
/// </summary>
public static class FeedbackRenderer
{
    public const string PlayIcon = "imgs/icons/play.svg";
    public const string PauseIcon = "imgs/icons/pause.svg";
    public const string NoMediaText = "No media";

    /// <summary>The bar's layout range is 0..1000.</summary>
    public const int BarRange = 1000;

    public static FeedbackFrame Render(NowPlayingSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.State == PlaybackState.None)
        {
            return new FeedbackFrame(null, NoMediaText, "", false, 0, "", "");
        }

        var icon = snapshot.State is PlaybackState.Playing or PlaybackState.Changing ? PlayIcon : PauseIcon;

        // The artist takes the narrow row beside the icon and the title the
        // full-width row below it: the title is the line that needs the room.
        var track = snapshot.Title;
        var artist = snapshot.Artist;
        if (track.Length == 0 && artist.Length == 0)
        {
            track = AppDisplayName(snapshot.AppId);
        }

        var barEnabled = false;
        var barValue = 0;
        var elapsed = "";
        var total = "";
        if (snapshot.Duration is { } duration && duration > TimeSpan.Zero && snapshot.EffectivePosition(now) is { } position)
        {
            barEnabled = true;
            barValue = (int)Math.Clamp(Math.Round(position.Ticks * (double)BarRange / duration.Ticks), 0, BarRange);
            elapsed = Clock(position);
            total = Clock(duration);
        }

        return new FeedbackFrame(icon, track, artist, barEnabled, barValue, elapsed, total);
    }

    /// <summary>
    /// The setFeedback payload that turns <paramref name="previous"/> into
    /// <paramref name="next"/>. A null previous frame produces a full payload.
    /// Empty when nothing changed, in which case nothing should be sent.
    /// </summary>
    public static Dictionary<string, object> Diff(FeedbackFrame? previous, FeedbackFrame next)
    {
        var payload = new Dictionary<string, object>();

        if (previous is null || previous.Icon != next.Icon)
        {
            payload["icon"] = next.Icon is null
                ? new Dictionary<string, object> { ["enabled"] = false }
                : new Dictionary<string, object> { ["enabled"] = true, ["value"] = next.Icon };
        }

        if (previous is null || previous.Track != next.Track)
        {
            payload["track"] = next.Track;
        }

        if (previous is null || previous.Artist != next.Artist)
        {
            payload["artist"] = next.Artist;
        }

        if (previous is null || previous.BarEnabled != next.BarEnabled || previous.BarValue != next.BarValue)
        {
            payload["progress"] = new Dictionary<string, object>
            {
                ["enabled"] = next.BarEnabled,
                ["value"] = next.BarValue,
            };
        }

        if (previous is null || previous.Elapsed != next.Elapsed)
        {
            payload["elapsed"] = next.Elapsed;
        }

        if (previous is null || previous.Total != next.Total)
        {
            payload["total"] = next.Total;
        }

        return payload;
    }

    /// <summary>m:ss, or h:mm:ss from one hour. Whole seconds, floored, never negative.</summary>
    public static string Clock(TimeSpan value)
    {
        var totalSeconds = Math.Max(0, (long)Math.Floor(value.TotalSeconds));
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }

    /// <summary>A readable name from a SourceAppUserModelId, for sessions with no metadata yet.</summary>
    public static string AppDisplayName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "";
        }

        var name = appId;
        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang]; // AppleInc.AppleMusicWin_nzyj5cx40ttqa!App
        }

        var underscore = name.IndexOf('_');
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4]; // foobar2000.exe
        }

        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..]; // AppleInc.AppleMusicWin
        }

        return name;
    }
}
