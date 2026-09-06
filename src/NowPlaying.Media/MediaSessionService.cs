using System.Security.Cryptography;
using System.Threading.Channels;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace NowPlaying.Media;

/// <summary>
/// Event-driven wrapper over Windows media sessions (README section 5).
///
/// Every WinRT event handler only posts a message to a channel; a single
/// consumer loop does all the reading, ranking, and publishing, so nothing
/// needs a lock and snapshot publication is serialised. Every awaited media
/// call is bounded by <see cref="MediaSessionServiceOptions.CallTimeout"/>.
/// </summary>
public sealed class MediaSessionService : IMediaSessionService
{
    private readonly MediaSessionServiceOptions _options;
    private readonly Channel<Message> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Tracked> _sessions = new();
    private readonly object _currentGate = new();
    private readonly ResumeCorrector _resumeCorrector = new();

    private Task? _loop;
    private Timer? _refreshTimer;
    private SessionManager? _manager;
    private TypedEventHandler<SessionManager, CurrentSessionChangedEventArgs>? _currentChangedHandler;
    private TypedEventHandler<SessionManager, SessionsChangedEventArgs>? _sessionsChangedHandler;
    private Tracked? _chosen;
    private DateTimeOffset? _changingSince;
    private CancellationTokenSource? _metadataDebounce;
    private NowPlayingSnapshot _current = NowPlayingSnapshot.Empty;
    private Artwork? _currentArtwork;
    private volatile IReadOnlyList<string> _knownAppIds = Array.Empty<string>();
    private string? _preferredAppId;
    private int _started;

    public MediaSessionService(MediaSessionServiceOptions? options = null)
    {
        _options = options ?? new MediaSessionServiceOptions();
        _preferredAppId = string.IsNullOrEmpty(_options.PreferredAppId) ? null : _options.PreferredAppId;
        _queue = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public NowPlayingSnapshot Current
    {
        get
        {
            lock (_currentGate)
            {
                return _current;
            }
        }
    }

    public Artwork? CurrentArtwork
    {
        get
        {
            lock (_currentGate)
            {
                return _currentArtwork;
            }
        }
    }

    public IReadOnlyList<string> KnownAppIds => _knownAppIds;

    public event Action<NowPlayingSnapshot>? SnapshotChanged;

    public event Action<Artwork?>? ArtworkChanged;

    public void SetPreferredAppId(string? appId) => Post(new SetPreferred(string.IsNullOrEmpty(appId) ? null : appId));

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _loop = Task.Run(() => RunLoopAsync(_stopping.Token), CancellationToken.None);
        _refreshTimer = new Timer(
            _ => Post(new Refresh(Full: false, Done: null)),
            null,
            _options.RefreshInterval,
            _options.RefreshInterval);
        return RefreshAsync(full: true, cancellationToken);
    }

    public Task RefreshAsync(bool full, CancellationToken cancellationToken = default)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new Refresh(full, done)))
        {
            done.TrySetException(new ObjectDisposedException(nameof(MediaSessionService)));
        }

        return done.Task.WaitAsync(cancellationToken);
    }

    public Task<bool> NextAsync() => SendAsync(CommandKind.Next);

    public Task<bool> PreviousAsync() => SendAsync(CommandKind.Previous);

    public Task<bool> TogglePlayPauseAsync() => SendAsync(CommandKind.Toggle);

    public async ValueTask DisposeAsync()
    {
        _refreshTimer?.Dispose();
        _stopping.Cancel();
        _queue.Writer.TryComplete();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch
            {
                // The loop's own errors are already logged.
            }
        }

        DetachManager();
        foreach (var tracked in _sessions)
        {
            Detach(tracked);
        }

        _sessions.Clear();
        _chosen = null;
        _stopping.Dispose();
    }

    // -- the loop ---------------------------------------------------------

    private bool Post(Message message) => _queue.Writer.TryWrite(message);

    private Task<bool> SendAsync(CommandKind kind)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new Command(kind, result)))
        {
            result.TrySetResult(false);
        }

        return result.Task;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(message, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"{message.GetType().Name} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task HandleAsync(Message message, CancellationToken ct)
    {
        switch (message)
        {
            case Refresh refresh:
                try
                {
                    await RefreshCoreAsync(refresh.Full, ct).ConfigureAwait(false);
                    refresh.Done?.TrySetResult();
                }
                catch (Exception ex)
                {
                    refresh.Done?.TrySetException(ex);
                    throw;
                }

                break;

            case SessionsChangedMessage:
                SyncSessions();
                await RerankAsync(ct).ConfigureAwait(false);
                break;

            case RerankMessage:
                await RerankAsync(ct).ConfigureAwait(false);
                break;

            case PlaybackChanged playback:
                if (_sessions.Contains(playback.Source))
                {
                    await RerankAsync(ct).ConfigureAwait(false);
                }

                break;

            case MediaChanged media:
                if (media.Source == _chosen)
                {
                    ScheduleMetadataRead(media.Source);
                }

                break;

            case ReadMetadata read:
                if (read.Source == _chosen)
                {
                    await ReadMetadataAsync(read.Source, ct).ConfigureAwait(false);
                    Publish();
                }

                break;

            case TimelineChanged timeline:
                if (timeline.Source == _chosen)
                {
                    Publish();
                }

                break;

            case Command command:
                command.Result.TrySetResult(await ExecuteAsync(command.Kind, ct).ConfigureAwait(false));
                break;

            case SetPreferred preferred:
                if (preferred.AppId != _preferredAppId)
                {
                    _preferredAppId = preferred.AppId;
                    Log($"preferred app: {preferred.AppId ?? "<automatic>"}");
                    await RerankAsync(ct).ConfigureAwait(false);
                }

                break;
        }
    }

    // -- manager and session bookkeeping ----------------------------------

    private async Task RefreshCoreAsync(bool full, CancellationToken ct)
    {
        if (full || _manager is null)
        {
            DetachManager();
            foreach (var tracked in _sessions)
            {
                Detach(tracked);
            }

            _sessions.Clear();
            _chosen = null;
            _changingSince = null;

            _manager = await SessionManager.RequestAsync().AsTask().WaitAsync(_options.CallTimeout, ct).ConfigureAwait(false);
            _currentChangedHandler = (_, _) => Post(new RerankMessage());
            _sessionsChangedHandler = (_, _) => Post(new SessionsChangedMessage());
            _manager.CurrentSessionChanged += _currentChangedHandler;
            _manager.SessionsChanged += _sessionsChangedHandler;
            Log(full ? "session manager (re)acquired" : "session manager acquired");
        }

        SyncSessions();
        await RerankAsync(ct).ConfigureAwait(false);
    }

    private void DetachManager()
    {
        if (_manager is null)
        {
            return;
        }

        try
        {
            if (_currentChangedHandler is not null)
            {
                _manager.CurrentSessionChanged -= _currentChangedHandler;
            }

            if (_sessionsChangedHandler is not null)
            {
                _manager.SessionsChanged -= _sessionsChangedHandler;
            }
        }
        catch (Exception ex)
        {
            Log($"detaching manager events failed: {ex.Message}");
        }

        _currentChangedHandler = null;
        _sessionsChangedHandler = null;
        _manager = null;
    }

    /// <summary>
    /// Bring the tracked list in line with what Windows reports. Indexed
    /// rather than foreach: the list can change underneath us when an app
    /// closes, and one unreadable entry must not hide the others.
    /// </summary>
    private void SyncSessions()
    {
        if (_manager is null)
        {
            return;
        }

        IReadOnlyList<Session> list;
        try
        {
            list = _manager.GetSessions();
        }
        catch (Exception ex)
        {
            Log($"GetSessions failed: {ex.Message}");
            return;
        }

        var next = new List<Tracked>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            Session session;
            string appId;
            try
            {
                session = list[i];
                appId = session.SourceAppUserModelId ?? "";
            }
            catch
            {
                continue;
            }

            if (next.Any(t => t.AppId == appId))
            {
                continue; // a second session from the same app: keep the first
            }

            var existing = _sessions.FirstOrDefault(t => t.AppId == appId);
            if (existing is not null && ReferenceEquals(existing.Session, session))
            {
                next.Add(existing);
                continue;
            }

            if (existing is not null)
            {
                // Same app, different session object: the app restarted. The
                // old object's events are dead, so start over with the new one.
                Log($"session replaced: {appId}");
                Detach(existing);
                if (_chosen == existing)
                {
                    _chosen = null;
                }
            }

            var tracked = new Tracked(session, appId);
            tracked.PlaybackHandler = (_, _) => Post(new PlaybackChanged(tracked));
            try
            {
                session.PlaybackInfoChanged += tracked.PlaybackHandler;
            }
            catch (Exception ex)
            {
                Log($"subscribing {appId} failed: {ex.Message}");
                continue;
            }

            Log($"session added: {appId}");
            next.Add(tracked);
        }

        foreach (var gone in _sessions.Where(t => !next.Contains(t) && !t.Detached).ToList())
        {
            Log($"session gone: {gone.AppId}");
            Detach(gone);
            if (_chosen == gone)
            {
                _chosen = null;
            }
        }

        _sessions.Clear();
        _sessions.AddRange(next);
        _knownAppIds = next.Select(t => t.AppId).ToArray();
    }

    private void Detach(Tracked tracked)
    {
        try
        {
            if (tracked.PlaybackHandler is not null)
            {
                tracked.Session.PlaybackInfoChanged -= tracked.PlaybackHandler;
            }

            DetachChosenEvents(tracked);
        }
        catch (Exception ex)
        {
            Log($"detaching {tracked.AppId} failed: {ex.Message}");
        }

        tracked.PlaybackHandler = null;
        tracked.Detached = true;
    }

    private void AttachChosenEvents(Tracked tracked)
    {
        tracked.MediaHandler = (_, _) => Post(new MediaChanged(tracked));
        tracked.TimelineHandler = (_, _) => Post(new TimelineChanged(tracked));
        tracked.Session.MediaPropertiesChanged += tracked.MediaHandler;
        tracked.Session.TimelinePropertiesChanged += tracked.TimelineHandler;
    }

    private void DetachChosenEvents(Tracked tracked)
    {
        if (tracked.MediaHandler is not null)
        {
            tracked.Session.MediaPropertiesChanged -= tracked.MediaHandler;
            tracked.MediaHandler = null;
        }

        if (tracked.TimelineHandler is not null)
        {
            tracked.Session.TimelinePropertiesChanged -= tracked.TimelineHandler;
            tracked.TimelineHandler = null;
        }
    }

    // -- choosing and reading ---------------------------------------------

    private async Task RerankAsync(CancellationToken ct)
    {
        string? currentId = null;
        try
        {
            currentId = _manager?.GetCurrentSession()?.SourceAppUserModelId;
        }
        catch
        {
            // No current session, or it vanished mid-call.
        }

        var now = DateTimeOffset.UtcNow;
        var facts = new List<SessionFacts>(_sessions.Count);
        foreach (var tracked in _sessions)
        {
            var status = ReadStatus(tracked);
            if (tracked == _chosen)
            {
                if (status == SessionStatus.Changing)
                {
                    // A track change passes through Changing; keep the rank the
                    // session had so the display does not flash "No media".
                    _changingSince ??= now;
                    if (now - _changingSince.Value < _options.ChangingGrace)
                    {
                        status = tracked.LastStableStatus;
                    }
                }
                else
                {
                    _changingSince = null;
                }
            }

            facts.Add(new SessionFacts(tracked.AppId, status, tracked.AppId.Length > 0 && tracked.AppId == currentId));
        }

        var winnerFacts = SessionRanking.Choose(facts, _preferredAppId);
        var winner = winnerFacts is null ? null : _sessions.First(t => t.AppId == winnerFacts.AppId);

        if (winner != _chosen)
        {
            if (_chosen is not null)
            {
                DetachChosenEvents(_chosen);
            }

            CancelMetadataRead();
            _chosen = winner;
            _changingSince = null;
            Log($"chosen: {winner?.AppId ?? "<none>"}");
            if (winner is not null)
            {
                AttachChosenEvents(winner);
                await ReadMetadataAsync(winner, ct).ConfigureAwait(false);
            }
        }
        else if (winner is not null && !winner.MetadataRead)
        {
            await ReadMetadataAsync(winner, ct).ConfigureAwait(false);
        }

        Publish();
    }

    private SessionStatus ReadStatus(Tracked tracked)
    {
        try
        {
            var info = tracked.Session.GetPlaybackInfo();
            var status = Map(info.PlaybackStatus);
            tracked.Status = status;
            if (status != SessionStatus.Changing)
            {
                tracked.LastStableStatus = status;
            }

            tracked.CanNext = info.Controls.IsNextEnabled;
            tracked.CanPrevious = info.Controls.IsPreviousEnabled;
            tracked.CanToggle = info.Controls.IsPlayPauseToggleEnabled;
            return status;
        }
        catch
        {
            tracked.Status = SessionStatus.Unreadable;
            return SessionStatus.Unreadable;
        }
    }

    private static SessionStatus Map(PlaybackStatus status) => status switch
    {
        PlaybackStatus.Closed => SessionStatus.Closed,
        PlaybackStatus.Opened => SessionStatus.Opened,
        PlaybackStatus.Changing => SessionStatus.Changing,
        PlaybackStatus.Stopped => SessionStatus.Stopped,
        PlaybackStatus.Playing => SessionStatus.Playing,
        PlaybackStatus.Paused => SessionStatus.Paused,
        _ => SessionStatus.Unreadable,
    };

    private void ScheduleMetadataRead(Tracked tracked)
    {
        CancelMetadataRead();
        var cts = new CancellationTokenSource();
        _metadataDebounce = cts;
        _ = Task.Delay(_options.MetadataDebounce, cts.Token).ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    Post(new ReadMetadata(tracked));
                }

                cts.Dispose();
            },
            TaskScheduler.Default);
    }

    private void CancelMetadataRead()
    {
        var pending = _metadataDebounce;
        _metadataDebounce = null;
        if (pending is not null)
        {
            try
            {
                pending.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>The expensive read, made for the chosen session only, with one retry.</summary>
    private async Task ReadMetadataAsync(Tracked tracked, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var props = await tracked.Session.TryGetMediaPropertiesAsync().AsTask()
                    .WaitAsync(_options.CallTimeout, ct).ConfigureAwait(false);
                tracked.Title = (props.Title ?? "").Trim();
                tracked.Artist = (props.Artist ?? "").Trim();
                tracked.AlbumArtist = (props.AlbumArtist ?? "").Trim();
                tracked.AlbumTitle = (props.AlbumTitle ?? "").Trim();
                tracked.MetadataRead = true;
                tracked.Artwork = await ReadArtworkAsync(tracked, props.Thumbnail, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"metadata read for {tracked.AppId} failed (attempt {attempt}): {ex.GetType().Name}: {ex.Message}");
                if (attempt == 1)
                {
                    await Task.Delay(_options.MetadataRetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Fetch the thumbnail bytes. Bounded like every other media call; a
    /// failure means no artwork, never a failed metadata read. The previous
    /// artwork is reused when the bytes are unchanged, so a burst of
    /// MediaPropertiesChanged events does not churn consumers.
    /// </summary>
    private async Task<Artwork?> ReadArtworkAsync(Tracked tracked, IRandomAccessStreamReference? reference, CancellationToken ct)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask().WaitAsync(_options.CallTimeout, ct).ConfigureAwait(false);
            using var input = stream.AsStreamForRead();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct).WaitAsync(_options.CallTimeout, ct).ConfigureAwait(false);
            if (buffer.Length == 0)
            {
                return null;
            }

            var bytes = buffer.ToArray();
            var key = Convert.ToHexString(SHA1.HashData(bytes));
            if (tracked.Artwork?.Key == key)
            {
                return tracked.Artwork;
            }

            string? contentType = null;
            try
            {
                contentType = stream.ContentType;
            }
            catch
            {
            }

            Log($"artwork for {tracked.AppId}: {bytes.Length} bytes{(contentType is null ? "" : $", {contentType}")}");
            return new Artwork(key, bytes, contentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"artwork read for {tracked.AppId} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static NormalizedTimeline ReadTimeline(Tracked tracked)
    {
        try
        {
            var timeline = tracked.Session.GetTimelineProperties();
            return TimelineMath.Normalize(timeline.StartTime, timeline.EndTime, timeline.Position, timeline.LastUpdatedTime);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Build the snapshot for the chosen session, or null to keep the previous one.</summary>
    private NowPlayingSnapshot? BuildSnapshot()
    {
        var tracked = _chosen;
        if (tracked is null)
        {
            return NowPlayingSnapshot.Empty;
        }

        var status = ReadStatus(tracked);
        if (status == SessionStatus.Changing)
        {
            return null;
        }

        var state = status.ToPlaybackState();
        if (state == PlaybackState.None)
        {
            return NowPlayingSnapshot.Empty;
        }

        if (state == PlaybackState.Stopped && tracked.Title.Length == 0 && tracked.Artist.Length == 0)
        {
            return NowPlayingSnapshot.Empty; // stopped with nothing loaded
        }

        var timeline = ReadTimeline(tracked);
        var artist = tracked.Artist.Length > 0 ? tracked.Artist
            : tracked.AlbumArtist.Length > 0 ? tracked.AlbumArtist
            : tracked.AlbumTitle;

        return new NowPlayingSnapshot(
            tracked.AppId,
            state,
            tracked.Title,
            artist,
            timeline.Position,
            timeline.PositionAt,
            timeline.Duration,
            tracked.CanNext,
            tracked.CanPrevious,
            tracked.CanToggle);
    }

    /// <summary>
    /// Update <see cref="Current"/> and raise <see cref="SnapshotChanged"/>
    /// for changes worth reacting to. Apple Music refreshes its timeline
    /// twice a second; those updates land in <see cref="Current"/> silently,
    /// because consumers already tick once a second for smooth progress. The
    /// event fires when anything other than the reported position changed,
    /// or when the position jumped (a seek).
    /// </summary>
    private void Publish()
    {
        var raw = BuildSnapshot();
        if (raw is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        NowPlayingSnapshot previous;
        lock (_currentGate)
        {
            previous = _current;
        }

        var snapshot = _resumeCorrector.Apply(previous, raw, now);

        var significant = false;
        lock (_currentGate)
        {
            if (snapshot != _current)
            {
                significant = IsSignificantChange(_current, snapshot, now);
                _current = snapshot;
            }
        }

        if (significant)
        {
            SnapshotChanged?.Invoke(snapshot);
        }

        // Artwork can change while the snapshot does not (the thumbnail
        // usually arrives a moment after the text), so it is tracked apart.
        var artwork = snapshot.State == PlaybackState.None ? null : _chosen?.Artwork;
        bool artworkChanged;
        lock (_currentGate)
        {
            artworkChanged = artwork?.Key != _currentArtwork?.Key;
            if (artworkChanged)
            {
                _currentArtwork = artwork;
            }
        }

        if (artworkChanged)
        {
            ArtworkChanged?.Invoke(artwork);
        }
    }

    /// <summary>A change is significant unless only the reported position moved by less than <see cref="SeekThreshold"/>.</summary>
    internal static bool IsSignificantChange(NowPlayingSnapshot previous, NowPlayingSnapshot next, DateTimeOffset now)
    {
        var previousAligned = previous with { Position = next.Position, PositionAt = next.PositionAt };
        if (previousAligned != next)
        {
            return true; // state, text, duration, or a control flag changed
        }

        var before = previous.EffectivePosition(now);
        var after = next.EffectivePosition(now);
        if (before is null || after is null)
        {
            return before != after;
        }

        var jump = after.Value - before.Value;
        return jump < -SeekThreshold || jump > SeekThreshold;
    }

    /// <summary>A position change beyond this, relative to the extrapolated position, is a seek and is published immediately.</summary>
    internal static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(2);

    private async Task<bool> ExecuteAsync(CommandKind kind, CancellationToken ct)
    {
        var tracked = _chosen;
        if (tracked is null)
        {
            Log($"{kind}: no session");
            return false;
        }

        IAsyncOperation<bool> operation;
        switch (kind)
        {
            case CommandKind.Next when tracked.CanNext:
                operation = tracked.Session.TrySkipNextAsync();
                break;
            case CommandKind.Previous when tracked.CanPrevious:
                operation = tracked.Session.TrySkipPreviousAsync();
                break;
            case CommandKind.Toggle when tracked.CanToggle:
                operation = tracked.Session.TryTogglePlayPauseAsync();
                break;
            default:
                Log($"{kind}: not enabled by {tracked.AppId}");
                return false;
        }

        try
        {
            var accepted = await operation.AsTask().WaitAsync(_options.CallTimeout, ct).ConfigureAwait(false);
            Log($"{kind}: {(accepted ? "accepted" : "rejected")} by {tracked.AppId}");
            return accepted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"{kind} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    // -- types ------------------------------------------------------------

    private sealed class Tracked(Session session, string appId)
    {
        public Session Session { get; } = session;
        public string AppId { get; } = appId;
        public SessionStatus Status;
        public SessionStatus LastStableStatus = SessionStatus.Unreadable;
        public bool CanNext;
        public bool CanPrevious;
        public bool CanToggle;
        public string Title = "";
        public string Artist = "";
        public string AlbumArtist = "";
        public string AlbumTitle = "";
        public bool MetadataRead;
        public bool Detached;
        public Artwork? Artwork;
        public TypedEventHandler<Session, PlaybackInfoChangedEventArgs>? PlaybackHandler;
        public TypedEventHandler<Session, MediaPropertiesChangedEventArgs>? MediaHandler;
        public TypedEventHandler<Session, TimelinePropertiesChangedEventArgs>? TimelineHandler;
    }

    private enum CommandKind
    {
        Next,
        Previous,
        Toggle,
    }

    private abstract record Message;

    private sealed record Refresh(bool Full, TaskCompletionSource? Done) : Message;

    private sealed record SessionsChangedMessage : Message;

    private sealed record RerankMessage : Message;

    private sealed record PlaybackChanged(Tracked Source) : Message;

    private sealed record MediaChanged(Tracked Source) : Message;

    private sealed record ReadMetadata(Tracked Source) : Message;

    private sealed record TimelineChanged(Tracked Source) : Message;

    private sealed record Command(CommandKind Kind, TaskCompletionSource<bool> Result) : Message;

    private sealed record SetPreferred(string? AppId) : Message;
}
