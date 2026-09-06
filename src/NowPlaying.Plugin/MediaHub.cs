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
    private static MediaSessionService? _service;
    private static int _attached;

    /// <summary>Raised on the service's loop whenever the snapshot changes.</summary>
    public static event Action<NowPlayingSnapshot>? SnapshotChanged;

    public static NowPlayingSnapshot Current => _service?.Current ?? NowPlayingSnapshot.Empty;

    public static void Attach()
    {
        lock (Gate)
        {
            _attached++;
            if (_service is not null)
            {
                return;
            }

            var service = new MediaSessionService(new MediaSessionServiceOptions
            {
                Log = message => Logger.Instance.LogMessage(TracingLevel.INFO, $"[media] {message}"),
            });
            service.SnapshotChanged += snapshot => SnapshotChanged?.Invoke(snapshot);
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

    public static Task<bool> NextAsync() => _service?.NextAsync() ?? Task.FromResult(false);

    public static Task<bool> PreviousAsync() => _service?.PreviousAsync() ?? Task.FromResult(false);

    public static Task<bool> TogglePlayPauseAsync() => _service?.TogglePlayPauseAsync() ?? Task.FromResult(false);

    public static Task RefreshAsync(bool full) => _service?.RefreshAsync(full) ?? Task.CompletedTask;
}
