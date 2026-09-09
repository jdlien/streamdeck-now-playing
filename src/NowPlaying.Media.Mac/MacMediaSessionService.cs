using System.Runtime.Versioning;

namespace NowPlaying.Media;

/// <summary>
/// <see cref="IMediaSessionService"/> on macOS: a router over two providers.
///
/// <code>
/// MRMediaRemoteGetNowPlayingClient  -->  owning bundle id
///         |
///         +-- com.apple.Music  -->  AppleScript
///         +-- anything else    -->  MediaRemote metadata and notifications
/// </code>
///
/// MediaRemote covers everything with no per-app work and pushes change
/// notifications, so the Windows design's event-driven shape survives. It has
/// one hole: it will not give up Music.app's metadata, and rather than failing
/// it never calls back, so every call into it is bounded and Music is asked
/// directly instead. The call that names the owning app answers even for Music,
/// which is what makes the routing possible at all.
///
/// What is lost against Windows: this reports the session macOS has selected,
/// not every session, so ranking across simultaneous players is the system's
/// choice rather than ours. <see cref="SetPreferredAppId"/> can override it for
/// an app that can be asked directly.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacMediaSessionService : IMediaSessionService
{
    private readonly MediaRemoteHost _host;
    private readonly MusicAppAdapter _music;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<string> _seenApps = [];

    private NowPlayingSnapshot _current = NowPlayingSnapshot.Empty;
    private Artwork? _artwork;
    private string? _artworkKey;
    private string? _preferredAppId;
    private string? _route;          // bundle id the metadata is currently coming from
    private bool _musicRouted;       // Music answers, MediaRemote does not
    private Task? _musicPoller;
    private bool _started;
    private DateTimeOffset _playingSince = DateTimeOffset.MinValue;

    /// <summary>
    /// How long a playing session keeps the display after macOS names a
    /// different, non-playing app as current. The Windows build ranks sessions
    /// itself and puts Playing above Paused (README 5.3); here the OS does the
    /// ranking and sometimes hands over a paused app while another is still
    /// playing, so this restores the same preference.
    /// </summary>
    private static readonly TimeSpan PlayingStickiness = TimeSpan.FromSeconds(5);

    public MacMediaSessionService(MediaSessionServiceOptions? options = null)
    {
        _log = options?.Log;
        _preferredAppId = options?.PreferredAppId;
        _music = new MusicAppAdapter(_log);
        _host = new MediaRemoteHost(_log);
        _host.StateChanged += state =>
        {
            try
            {
                OnHostState(state);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"handling a helper state failed: {ex.Message}");
            }
        };
        _host.ArtworkReceived += (key, bytes) =>
        {
            try
            {
                OnHostArtwork(key, bytes);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"handling helper artwork failed: {ex.Message}");
            }
        };
    }

    public NowPlayingSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public Artwork? CurrentArtwork
    {
        get
        {
            lock (_gate)
            {
                return _artwork;
            }
        }
    }

    public IReadOnlyList<string> KnownAppIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _seenApps];
            }
        }
    }

    public event Action<NowPlayingSnapshot>? SnapshotChanged;

    public event Action<Artwork?>? ArtworkChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            _started = true;
        }

        if (MediaRemoteHost.HelperPath is null)
        {
            _log?.Invoke("the MediaRemote helper is not installed; only Music.app will be visible");
        }

        _host.Start();
        _musicPoller ??= Task.Run(() => PollMusicAsync(_cancellation.Token));
        _ = _music.ProbeAutomationAsync(_cancellation.Token);
        return Task.CompletedTask;
    }

    public Task RefreshAsync(bool full, CancellationToken cancellationToken = default)
    {
        _host.Send("refresh");
        return Task.CompletedTask;
    }

    public void SetPreferredAppId(string? appId)
    {
        lock (_gate)
        {
            _preferredAppId = string.IsNullOrWhiteSpace(appId) ? null : appId;
        }

        _host.Send("refresh");
    }

    public async Task<bool> NextAsync() => await CommandAsync("next").ConfigureAwait(false);

    public async Task<bool> PreviousAsync() => await CommandAsync("previous").ConfigureAwait(false);

    public async Task<bool> TogglePlayPauseAsync() => await CommandAsync("toggle").ConfigureAwait(false);

    /// <summary>
    /// Commands follow the metadata. Sending a global MediaRemote command while
    /// showing Music's metadata could move a different player, which is the
    /// mismatch the router exists to avoid.
    /// </summary>
    private async Task<bool> CommandAsync(string command)
    {
        bool viaMusic;
        lock (_gate)
        {
            viaMusic = _musicRouted;
        }

        if (viaMusic)
        {
            var ok = command switch
            {
                "next" => await _music.NextAsync().ConfigureAwait(false),
                "previous" => await _music.PreviousAsync().ConfigureAwait(false),
                _ => await _music.ToggleAsync().ConfigureAwait(false),
            };

            if (ok)
            {
                await PublishMusicAsync(_cancellation.Token).ConfigureAwait(false);
            }

            return ok;
        }

        if (!_host.IsRunning)
        {
            return false;
        }

        _host.Send(command);
        return true;
    }

    private void OnHostState(MediaRemoteState state)
    {
        if (!string.IsNullOrEmpty(state.Bundle))
        {
            lock (_gate)
            {
                _seenApps.Add(state.Bundle);
            }
        }

        // Hold a playing session against a switch to one that is merely paused.
        // Without this the strip can move to another app between a press and the
        // command it sends, and the press lands on the wrong player.
        lock (_gate)
        {
            var incomingIsPlaying = state.Playing || state.Rate is > 0;
            var differentApp = !string.Equals(state.Bundle, _current.AppId, StringComparison.OrdinalIgnoreCase);
            var holdingPlaying = _current.State == PlaybackState.Playing
                && DateTimeOffset.UtcNow - _playingSince < PlayingStickiness;

            if (differentApp && !incomingIsPlaying && holdingPlaying && !string.IsNullOrEmpty(_current.AppId))
            {
                _log?.Invoke($"ignoring a switch to paused {state.Bundle}; {_current.AppId} is still playing");
                return;
            }

            if (incomingIsPlaying)
            {
                _playingSince = DateTimeOffset.UtcNow;
            }
        }

        // Route to Music when it owns the session and MediaRemote would not
        // answer, or when it has been pinned as the preferred player.
        var preferMusic = string.Equals(_preferredAppId, MusicAppAdapter.BundleId, StringComparison.OrdinalIgnoreCase);
        var musicOwns = string.Equals(state.Bundle, MusicAppAdapter.BundleId, StringComparison.OrdinalIgnoreCase);
        var route = (musicOwns && state.Stale) || preferMusic;

        lock (_gate)
        {
            _musicRouted = route;
            _route = state.Bundle;
        }

        if (route)
        {
            _ = PublishMusicAsync(_cancellation.Token);
            return;
        }

        Publish(FromMediaRemote(state));

        if (state.ArtworkKey is { Length: > 0 } key)
        {
            lock (_gate)
            {
                if (key == _artworkKey)
                {
                    return;
                }
            }

            _host.Send($"artwork {key}");
        }
        else
        {
            SetArtwork(null, null);
        }
    }

    private void OnHostArtwork(string key, byte[] bytes)
    {
        lock (_gate)
        {
            if (_musicRouted)
            {
                return;   // Music's artwork comes from Music
            }
        }

        SetArtwork(key, bytes);
    }

    private static NowPlayingSnapshot FromMediaRemote(MediaRemoteState state)
    {
        if (string.IsNullOrEmpty(state.Bundle))
        {
            return NowPlayingSnapshot.Empty;
        }

        // Rate is the honest signal: a paused player reports 0 while still
        // holding the session.
        var playing = state.Rate is > 0 || (state.Rate is null && state.Playing);
        var status = playing ? PlaybackState.Playing : PlaybackState.Paused;
        if (state is { Title: null or "", Artist: null or "" } && !state.Playing)
        {
            status = PlaybackState.Stopped;
        }

        var duration = state.Duration is > 0 ? TimeSpan.FromSeconds(state.Duration.Value) : (TimeSpan?)null;
        TimeSpan? position = state.Elapsed is >= 0 ? TimeSpan.FromSeconds(state.Elapsed.Value) : null;
        DateTimeOffset? positionAt = state.ElapsedAt is > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(state.ElapsedAt.Value * 1000))
            : (position is null ? null : DateTimeOffset.UtcNow);

        return new NowPlayingSnapshot(
            state.Bundle,
            status,
            Clean(state.Title),
            // Not MetadataNormalizer.Artist: that strips an " -- Album" suffix
            // out of the artist because Windows' Apple Music packs both into one
            // field. MediaRemote reports artist and album separately, so applying
            // it here would truncate an artist that legitimately contains a dash.
            Clean(string.IsNullOrWhiteSpace(state.Artist) ? state.Album : state.Artist),
            position,
            positionAt,
            duration,
            CanNext: true,
            CanPrevious: true,
            CanToggle: true);
    }

    /// <summary>
    /// Music has no change notifications reachable from here, so while it owns
    /// the session it is polled. Only while it owns the session: everything else
    /// arrives as an event, and polling an app that is not being shown would
    /// cost for nothing.
    /// </summary>
    private async Task PollMusicAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool routed;
            lock (_gate)
            {
                routed = _musicRouted;
            }

            // If the helper is down, Music is the only thing left that can be
            // asked. That covers a crash loop and, more importantly, the day an
            // OS update takes MediaRemote away entirely: the sibling ak820-pro
            // agent chose AppleScript precisely because Apple keeps restricting
            // MediaRemote, so losing it should narrow this plugin rather than
            // blank it.
            if (!routed && !_host.IsRunning && MediaRemoteHost.HelperPath is not null)
            {
                routed = true;
                lock (_gate)
                {
                    _musicRouted = true;
                }
            }

            var interval = TimeSpan.FromSeconds(routed ? (Current.State == PlaybackState.Playing ? 1 : 5) : 10);
            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!routed)
            {
                continue;
            }

            try
            {
                await PublishMusicAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // This loop is the only thing that updates the display while
                // Music owns the session: the helper cannot report Music's
                // metadata, so it emits an identical line for every track and
                // no event will restart this. Letting an exception escape here
                // froze the dial permanently on whatever was playing at the
                // time, which is exactly how it was found.
                _log?.Invoke($"Music poll failed, continuing: {ex.Message}");
            }
        }
    }

    private async Task PublishMusicAsync(CancellationToken token)
    {
        var outcome = await _music.ReadAsync(token).ConfigureAwait(false);

        // A query that failed says nothing about what Music is doing. Treating
        // it as "stopped" gave up the route, and because the helper cannot
        // report Music's metadata it emits an identical line for every track
        // and never prompts a re-route, so the dial froze until the plugin was
        // restarted. Keep the route and the last snapshot; try again next tick.
        if (outcome.Status == MusicReadStatus.Failed)
        {
            return;
        }

        if (outcome.Status == MusicReadStatus.NotPlaying || outcome.State is null)
        {
            // Music really has nothing on. Hand back to MediaRemote if it is
            // there; if it is not, there is nothing else to ask.
            lock (_gate)
            {
                _musicRouted = false;
            }

            if (_host.IsRunning)
            {
                _host.Send("refresh");
            }
            else
            {
                Publish(NowPlayingSnapshot.Empty);
                SetArtwork(null, null);
            }

            return;
        }

        var state = outcome.State;

        var snapshot = new NowPlayingSnapshot(
            MusicAppAdapter.BundleId,
            state.State,
            Clean(state.Title),
            Clean(string.IsNullOrEmpty(state.Artist) ? state.Album : state.Artist),
            state.Position,
            state.Position is null ? null : DateTimeOffset.UtcNow,
            state.Duration,
            CanNext: true,
            CanPrevious: true,
            CanToggle: true);

        Publish(snapshot);

        // Artwork is keyed by the track, so it is fetched once per track rather
        // than on every poll: reading it costs an AppleScript round trip and a
        // temp file.
        string? currentKey;
        lock (_gate)
        {
            currentKey = _artworkKey;
        }

        var key = $"music:{state.TrackId}";
        if (key != currentKey && state.State != PlaybackState.Stopped)
        {
            var bytes = await _music.ReadArtworkAsync(token).ConfigureAwait(false);
            SetArtwork(bytes is null ? null : key, bytes);
        }
    }

    /// <summary>Trim and collapse a metadata field; null becomes empty, as the snapshot expects.</summary>
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    private void Publish(NowPlayingSnapshot snapshot)
    {
        lock (_gate)
        {
            if (!SnapshotChange.IsSignificant(_current, snapshot, DateTimeOffset.UtcNow))
            {
                return;
            }

            _current = snapshot;
        }

        _log?.Invoke($"{snapshot.AppId ?? "nothing"}: {snapshot.State} \"{snapshot.Title}\"");
        Raise(() => SnapshotChanged?.Invoke(snapshot), nameof(SnapshotChanged));
    }

    /// <summary>
    /// Raise an event without letting a subscriber's failure escape into the
    /// loop that raised it. Both publishers here run on long-lived tasks that
    /// nothing restarts.
    /// </summary>
    private void Raise(Action raise, string name)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"a {name} handler threw: {ex.Message}");
        }
    }

    private void SetArtwork(string? key, byte[]? bytes)
    {
        Artwork? artwork = null;
        lock (_gate)
        {
            if (key == _artworkKey)
            {
                return;
            }

            _artworkKey = key;
            _artwork = bytes is { Length: > 0 } ? new Artwork(key ?? "", bytes, null) : null;
            artwork = _artwork;
        }

        Raise(() => ArtworkChanged?.Invoke(artwork), nameof(ArtworkChanged));
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);
        await _host.DisposeAsync().ConfigureAwait(false);

        if (_musicPoller is not null)
        {
            try
            {
                await _musicPoller.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
    }
}
