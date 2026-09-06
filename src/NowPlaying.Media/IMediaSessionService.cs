namespace NowPlaying.Media;

/// <summary>
/// The one media subscription the plugin shares between all action instances
/// (README section 4). <see cref="MediaSessionService"/> implements it over
/// Windows.Media.Control; the console harness is its first consumer.
/// </summary>
public interface IMediaSessionService : IAsyncDisposable
{
    /// <summary>The latest published snapshot. Never blocks on the media API.</summary>
    NowPlayingSnapshot Current { get; }

    /// <summary>The chosen session's artwork, or null when there is none. Never blocks on the media API.</summary>
    Artwork? CurrentArtwork { get; }

    /// <summary>App ids of every session Windows currently reports, in the manager's order.</summary>
    IReadOnlyList<string> KnownAppIds { get; }

    /// <summary>Raised on the service's own loop whenever <see cref="Current"/> changes significantly.</summary>
    event Action<NowPlayingSnapshot>? SnapshotChanged;

    /// <summary>Raised on the service's own loop whenever <see cref="CurrentArtwork"/> changes.</summary>
    event Action<Artwork?>? ArtworkChanged;

    /// <summary>Start listening and complete once the first snapshot is in place. Later calls are no-ops.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-sync with Windows. A full refresh re-requests the session manager
    /// and rebuilds every subscription (after sleep/wake); a cheap one
    /// re-enumerates sessions and re-ranks.
    /// </summary>
    Task RefreshAsync(bool full, CancellationToken cancellationToken = default);

    /// <summary>Prefer this app whenever it has a candidate session; null or empty follows the ranking alone.</summary>
    void SetPreferredAppId(string? appId);

    /// <summary>Skip to the next track on the chosen session. False when there is none or the player refuses.</summary>
    Task<bool> NextAsync();

    /// <summary>Skip to the previous track on the chosen session. False when there is none or the player refuses.</summary>
    Task<bool> PreviousAsync();

    /// <summary>Toggle play/pause on the chosen session. False when there is none or the player refuses.</summary>
    Task<bool> TogglePlayPauseAsync();
}
