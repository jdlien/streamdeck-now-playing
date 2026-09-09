using Windows.Media.Control;

namespace NowPlaying.Media;

/// <summary>One Windows media session, as the diagnostic probe sees it.</summary>
public sealed record SessionProbe(
    string AppId,
    bool IsCurrent,
    string Status,
    string? PlaybackType,
    string Title,
    string Artist,
    string AlbumTitle,
    bool HasThumbnail,
    bool CanNext,
    bool CanPrevious,
    bool CanToggle,
    TimeSpan Position,
    TimeSpan StartTime,
    TimeSpan EndTime,
    DateTimeOffset LastUpdated);

/// <summary>
/// Reads every session Windows knows about, metadata and all. This is the
/// diagnostic path, the answer to "why doesn't this app show up?", and it is
/// deliberately separate from the service's hot path, which reads metadata for
/// the chosen session only (README section 5.5).
/// </summary>
public static class MediaSessionProbe
{
    public static async Task<(string? CurrentAppId, IReadOnlyList<SessionProbe> Sessions)> ReadAllAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        string? currentId = null;
        try
        {
            currentId = manager.GetCurrentSession()?.SourceAppUserModelId;
        }
        catch
        {
            // A current session can vanish between the call and the read.
        }

        // Indexed rather than foreach: the list can change underneath us when
        // an app closes, and one unreadable entry must not hide the others.
        var sessions = manager.GetSessions();
        var result = new List<SessionProbe>(sessions.Count);
        for (var i = 0; i < sessions.Count; i++)
        {
            GlobalSystemMediaTransportControlsSession session;
            try
            {
                session = sessions[i];
            }
            catch
            {
                continue;
            }

            result.Add(await ReadOneAsync(session, currentId));
        }

        return (currentId, result);
    }

    private static async Task<SessionProbe> ReadOneAsync(
        GlobalSystemMediaTransportControlsSession session, string? currentId)
    {
        var appId = "";
        try
        {
            appId = session.SourceAppUserModelId ?? "";
        }
        catch
        {
        }

        var status = "Unreadable";
        string? playbackType = null;
        var canNext = false;
        var canPrevious = false;
        var canToggle = false;
        try
        {
            var info = session.GetPlaybackInfo();
            status = info.PlaybackStatus.ToString();
            playbackType = info.PlaybackType?.ToString();
            canNext = info.Controls.IsNextEnabled;
            canPrevious = info.Controls.IsPreviousEnabled;
            canToggle = info.Controls.IsPlayPauseToggleEnabled;
        }
        catch
        {
        }

        var title = "";
        var artist = "";
        var album = "";
        var hasThumbnail = false;
        try
        {
            // A session can exist before its metadata arrives; that is not an error.
            var props = await session.TryGetMediaPropertiesAsync();
            title = props.Title ?? "";
            artist = props.Artist ?? "";
            album = props.AlbumTitle ?? "";
            hasThumbnail = props.Thumbnail is not null;
        }
        catch
        {
        }

        TimeSpan position = default, start = default, end = default;
        DateTimeOffset updated = default;
        try
        {
            var timeline = session.GetTimelineProperties();
            position = timeline.Position;
            start = timeline.StartTime;
            end = timeline.EndTime;
            updated = timeline.LastUpdatedTime;
        }
        catch
        {
        }

        return new SessionProbe(
            appId,
            IsCurrent: appId.Length > 0 && appId == currentId,
            status,
            playbackType,
            title.Trim(),
            artist.Trim(),
            album.Trim(),
            hasThumbnail,
            canNext,
            canPrevious,
            canToggle,
            position,
            start,
            end,
            updated);
    }
}
