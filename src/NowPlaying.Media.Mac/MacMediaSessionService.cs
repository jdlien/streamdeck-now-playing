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
/// notifications, so the Windows design's event-driven shape survives. It is
/// the source for every app that it will describe, Music included.
///
/// The AppleScript branch is the fallback, taken when the helper says it has an
/// owning app but no metadata for it, and whenever the helper is not answering
/// at all. It was built because Music.app appeared to be a permanent case of
/// the first kind; that turned out to be a deadlock in the helper rather than
/// anything about Music, and it is kept because a provider that names an app
/// without describing it is a real state, and because AppleScript is what
/// remains if a future macOS closes MediaRemote off entirely.
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
    private bool _recheckScheduled;  // a held-off switch will be looked at again
    private long _musicReadsStarted; // sequence of Music reads, so a slow one cannot land after a newer one
    private long _musicReadPublished;
    private DateTimeOffset _lastMusicHandback = DateTimeOffset.MinValue;

    /// <summary>
    /// Least time between two hand-backs from Music to MediaRemote. Each one
    /// asks the helper to republish, and if MediaRemote still names Music the
    /// route comes straight back: without a floor, the two ask each other as
    /// fast as an AppleScript round trip allows.
    /// </summary>
    private static readonly TimeSpan HandbackInterval = TimeSpan.FromSeconds(5);

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

        if (!_host.IsHealthy)
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
        //
        // A held-off switch is looked at again once the hold runs out. It used
        // to be dropped: the helper does not repeat a state it has already sent,
        // its 30-second backstop included, so nothing ever re-offered it, and
        // the strip went on showing the old player "playing" -- its bar running
        // past the end of the track -- until some unrelated change came along or
        // a replug forced a refresh. A session that has gone altogether (no app
        // at all) is not held: there is no player left to land a press on.
        var hold = false;
        TimeSpan? recheckIn = null;
        lock (_gate)
        {
            var incomingIsPlaying = state.Playing || state.Rate is > 0;
            var differentApp = !string.Equals(state.Bundle, _current.AppId, StringComparison.OrdinalIgnoreCase);
            var heldFor = DateTimeOffset.UtcNow - _playingSince;
            var holdingPlaying = _current.State == PlaybackState.Playing && heldFor < PlayingStickiness;

            if (differentApp && !incomingIsPlaying && holdingPlaying
                && !string.IsNullOrEmpty(_current.AppId) && !string.IsNullOrEmpty(state.Bundle))
            {
                hold = true;
                if (!_recheckScheduled)
                {
                    _recheckScheduled = true;
                    recheckIn = PlayingStickiness - heldFor + TimeSpan.FromMilliseconds(250);
                }
            }
            else if (incomingIsPlaying)
            {
                _playingSince = DateTimeOffset.UtcNow;
            }
        }

        if (recheckIn is { } delay)
        {
            _ = RecheckAfterAsync(delay);
        }

        if (hold)
        {
            _log?.Invoke($"holding off a switch to paused {state.Bundle}; {_current.AppId} is still playing");
            return;
        }

        // Route to Music when it owns the session and MediaRemote would not
        // answer, or when it has been pinned as the preferred player.
        var preferMusic = IsMusicPreferred;
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

    /// <summary>Ask the helper for its state again once a hold has run out; see <see cref="OnHostState"/>.</summary>
    private async Task RecheckAfterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_gate)
            {
                _recheckScheduled = false;
            }
        }

        // "refresh" makes the helper forget what it last sent and send it again,
        // so the state that was held off comes back to be judged afresh.
        _host.Send("refresh");
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

            // If the helper is not answering, Music is the only thing left that
            // can be asked. That covers a crash loop and, more importantly, the
            // day an OS update takes MediaRemote away entirely: the sibling
            // ak820-pro agent chose AppleScript precisely because Apple keeps
            // restricting MediaRemote, so losing it should narrow this plugin
            // rather than blank it.
            //
            // IsHealthy, not IsRunning. A helper whose queues have deadlocked
            // stays alive and silent, and asking whether the process exists said
            // yes right up to the point the dial had been frozen for an hour.
            if (!routed && !_host.IsHealthy && MediaRemoteHost.HelperPath is not null)
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
        // Reads come from the poll, from helper events and from commands, and
        // nothing serializes them. A read that finishes after a newer one has
        // already been published, or after the route has left Music, is old
        // news: publishing it put a stale track -- or Music itself -- back over
        // whatever had replaced it, and with the route gone nothing polled
        // Music again to correct it. So every result is checked and committed
        // in one step, under the lock the route changes under.
        var sequence = Interlocked.Increment(ref _musicReadsStarted);
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
            // there and may know of another player; otherwise say so.
            //
            // Not when Music is the preferred player: the hand-back's republish
            // is routed straight back to Music, which still has nothing on,
            // which hands back again -- an AppleScript a round trip, forever.
            // Pinned, Music keeps the route and the poll asks it again on its
            // own schedule. And never more often than the floor, because a
            // MediaRemote that still names Music does the same thing unpinned.
            bool handBack;
            lock (_gate)
            {
                if (!ClaimMusicReadLocked(sequence))
                {
                    return;
                }

                var pinned = IsMusicPreferred;
                _musicRouted = pinned;
                handBack = !pinned && _host.IsHealthy && DateTimeOffset.UtcNow - _lastMusicHandback >= HandbackInterval;
                if (handBack)
                {
                    _lastMusicHandback = DateTimeOffset.UtcNow;
                }
            }

            if (handBack)
            {
                _host.Send("refresh");
                return;
            }

            // Music having nothing on may clear Music's own display, never one
            // that another player has taken over in the meantime -- unless Music
            // is pinned, when the display is Music's whatever was on it before:
            // refusing then left the previous player's track up indefinitely
            // while every press went to Music.
            bool StillMusics() => Interlocked.Read(ref _musicReadPublished) == sequence
                && ((IsMusicPreferred && _musicRouted)
                    || string.IsNullOrEmpty(_current.AppId)
                    || string.Equals(_current.AppId, MusicAppAdapter.BundleId, StringComparison.OrdinalIgnoreCase));
            if (Publish(NowPlayingSnapshot.Empty, StillMusics))
            {
                SetArtwork(null, null, StillMusics);
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

        if (!Publish(snapshot, () => ClaimMusicReadLocked(sequence)))
        {
            return;
        }

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

            // The route or the track may have moved on while the art was read.
            SetArtwork(bytes is null ? null : key, bytes,
                () => _musicRouted && Interlocked.Read(ref _musicReadPublished) == sequence);
        }
    }

    private bool IsMusicPreferred =>
        string.Equals(_preferredAppId, MusicAppAdapter.BundleId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a finished Music read may be published: the route is still on
    /// Music and no newer read has been published first. Claims the slot when
    /// it may, so an older read finishing later is refused. Call under the gate.
    /// </summary>
    private bool ClaimMusicReadLocked(long sequence)
    {
        if (!_musicRouted || sequence < Interlocked.Read(ref _musicReadPublished))
        {
            return false;
        }

        Interlocked.Exchange(ref _musicReadPublished, sequence);
        return true;
    }

    /// <summary>Trim and collapse a metadata field; null becomes empty, as the snapshot expects.</summary>
    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim();

    /// <param name="snapshot">The new state.</param>
    /// <param name="stillValid">
    /// Checked under the same lock the snapshot is committed under, for a
    /// result that may have gone stale while it was being fetched.
    /// </param>
    /// <returns>False when <paramref name="stillValid"/> refused it.</returns>
    private bool Publish(NowPlayingSnapshot snapshot, Func<bool>? stillValid = null)
    {
        lock (_gate)
        {
            if (stillValid is not null && !stillValid())
            {
                return false;
            }

            if (!SnapshotChange.IsSignificant(_current, snapshot, DateTimeOffset.UtcNow))
            {
                return true;
            }

            _current = snapshot;
        }

        _log?.Invoke($"{snapshot.AppId ?? "nothing"}: {snapshot.State} \"{snapshot.Title}\"");
        Raise(() => SnapshotChanged?.Invoke(snapshot), nameof(SnapshotChanged));
        return true;
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

    private void SetArtwork(string? key, byte[]? bytes, Func<bool>? stillValid = null)
    {
        Artwork? artwork = null;
        lock (_gate)
        {
            if (key == _artworkKey || (stillValid is not null && !stillValid()))
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
