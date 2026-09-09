using BarRaider.SdTools;
using NowPlaying.Media;

namespace NowPlaying.Plugin;

/// <summary>
/// The single media subscription shared by every action instance (README
/// section 4). Created on the first instance and kept for the life of the
/// process: idle it costs nothing but a few event subscriptions.
/// </summary>
internal static class MediaHub
{
    private static readonly object Gate = new();
    private static IMediaSessionService? _service;
    private static int _attached;

    /// <summary>Raised on the service's loop whenever the snapshot changes significantly.</summary>
    public static event Action<NowPlayingSnapshot>? SnapshotChanged;

    /// <summary>Raised on the service's loop whenever the artwork changes.</summary>
    public static event Action<Artwork?>? ArtworkChanged;

    public static NowPlayingSnapshot Current => _service?.Current ?? NowPlayingSnapshot.Empty;

    public static Artwork? CurrentArtwork => _service?.CurrentArtwork;

    public static IReadOnlyList<string> KnownAppIds => _service?.KnownAppIds ?? Array.Empty<string>();

    public static void Attach()
    {
        lock (Gate)
        {
            _attached++;
            if (_service is not null)
            {
                return;
            }

            var service = PlatformServices.CreateMedia(new MediaSessionServiceOptions
            {
                Log = message => Logger.Instance.LogMessage(TracingLevel.INFO, $"[media] {message}"),
            });
            if (service is null)
            {
                Logger.Instance.LogMessage(TracingLevel.INFO, "[media] no implementation on this platform");
                return;
            }

            service.SnapshotChanged += snapshot => SnapshotChanged?.Invoke(snapshot);
            service.ArtworkChanged += artwork => ArtworkChanged?.Invoke(artwork);
            _service = service;

            _ = service.StartAsync().ContinueWith(
                task => Logger.Instance.LogMessage(
                    TracingLevel.ERROR,
                    $"[media] start failed: {task.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    public static void Detach()
    {
        lock (Gate)
        {
            _attached = Math.Max(0, _attached - 1);
        }
    }

    public static int AttachedCount
    {
        get
        {
            lock (Gate)
            {
                return _attached;
            }
        }
    }

    public static void SetPreferredAppId(string? appId) => _service?.SetPreferredAppId(appId);

    public static Task<bool> NextAsync() => _service?.NextAsync() ?? Task.FromResult(false);

    public static Task<bool> PreviousAsync() => _service?.PreviousAsync() ?? Task.FromResult(false);

    public static Task<bool> TogglePlayPauseAsync() => _service?.TogglePlayPauseAsync() ?? Task.FromResult(false);

    public static Task RefreshAsync(bool full) => _service?.RefreshAsync(full) ?? Task.CompletedTask;

    /// <summary>Run a named transport command; "none" succeeds without doing anything.</summary>
    public static Task<bool> RunAsync(string command) => command switch
    {
        "toggle" => TogglePlayPauseAsync(),
        "next" => NextAsync(),
        "previous" => PreviousAsync(),
        _ => Task.FromResult(true),
    };
}
