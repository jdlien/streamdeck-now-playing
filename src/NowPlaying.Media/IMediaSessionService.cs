namespace NowPlaying.Media;

/// <summary>
/// The one media subscription the plugin shares between all action instances
/// (README section 4). Milestone 1 implements this over
/// Windows.Media.Control; the console harness is its first consumer.
/// </summary>
public interface IMediaSessionService : IAsyncDisposable
{
    /// <summary>The latest published snapshot. Never blocks on the media API.</summary>
    NowPlayingSnapshot Current { get; }

    /// <summary>Raised on the service's own loop whenever <see cref="Current"/> changes.</summary>
    event Action<NowPlayingSnapshot>? SnapshotChanged;

    /// <summary>Start listening. Safe to call once; later calls are no-ops.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Skip to the next track on the chosen session, if the player allows it.</summary>
    Task<bool> NextAsync();

    /// <summary>Skip to the previous track on the chosen session, if the player allows it.</summary>
    Task<bool> PreviousAsync();

    /// <summary>Toggle play/pause on the chosen session, if the player allows it.</summary>
    Task<bool> TogglePlayPauseAsync();
}
