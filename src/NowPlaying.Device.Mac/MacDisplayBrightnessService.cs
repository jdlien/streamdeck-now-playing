using System.Runtime.Versioning;

namespace NowPlaying.Device;

/// <summary>
/// <see cref="IDisplayBrightnessService"/> on macOS. Same contract as the
/// Windows service, two differences of substance.
///
/// First, there is no single protocol: Apple panels answer only DisplayServices
/// and everything else answers only DDC/CI, so a backend is chosen per display
/// by probing, and a display that answers neither is reported unavailable
/// rather than guessed at (macos-port-plan D5).
///
/// Second, each display gets its own worker. The Windows service drives every
/// monitor from one thread, which means a wedged DDC transaction on one monitor
/// blocks the others; DDC round trips here are just as slow and moody, and one
/// of the three displays on the development machine is a third-party panel
/// sharing a bus with two Apple ones (macos-port-plan M4.5).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacDisplayBrightnessService : IDisplayBrightnessService
{
    /// <summary>Restore level when un-dimming from a level of 0, matching the Windows service.</summary>
    public const int RestoreFloor = 30;

    /// <summary>No reads this soon after a write: monitors report the old value for a moment.</summary>
    private static readonly TimeSpan QuietAfterWrite = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often a display whose reads are free is re-read. Apple panels report
    /// their own changes, so this is only a backstop for a missed notification.
    /// </summary>
    private static readonly TimeSpan IdleWake = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often a DDC/CI display is re-read to notice a change made elsewhere:
    /// its own buttons, another utility, anything that is not this plugin. DDC
    /// has no notification of any kind, so polling is the only way to see one.
    ///
    /// Frequent enough to feel current when glancing at the strip, and only ever
    /// while the panel is awake. Polling a sleeping one is what kept monitors
    /// from staying asleep on Windows, and that rule has not changed.
    /// </summary>
    private static readonly TimeSpan DdcPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Poll interval after a display stops answering, which usually means it was
    /// switched off at the monitor or to another input. There is no point asking
    /// at the normal rate, and each attempt costs a bus timeout.
    /// </summary>
    private static readonly TimeSpan DdcBackoff = TimeSpan.FromSeconds(60);

    /// <summary>Unanswered polls before backing off.</summary>
    private const int QuietPollsBeforeBackoff = 3;

    /// <summary>
    /// How long a display is left alone before it is re-read.
    /// </summary>
    /// <param name="readCostsABusTransaction">
    /// True for DDC/CI. Such a display has no way to announce a change, so the
    /// poll is the only way to see one; a display that answers locally announces
    /// its own changes and needs nothing more than a backstop.
    /// </param>
    /// <param name="quietPolls">Consecutive polls the display did not answer.</param>
    internal static TimeSpan PollInterval(bool readCostsABusTransaction, int quietPolls) =>
        !readCostsABusTransaction ? IdleWake
        : quietPolls >= QuietPollsBeforeBackoff ? DdcBackoff
        : DdcPoll;

    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly List<Channel> _channels = [];
    private readonly NativeMethods.DisplayReconfigurationCallback _onDisplaysChanged;

    /// <summary>
    /// Held in a field so the marshalled thunk outlives the native
    /// registration; a callback collected while DisplayServices still holds it
    /// takes the process down.
    /// </summary>
    private readonly NativeMethods.BrightnessChangeCallback _onBrightnessChanged;

    /// <summary>
    /// Displays currently registered for brightness-change notifications.
    ///
    /// Deliberately owned here and not by the channels. A registration is keyed
    /// by (display, context) inside DisplayServices, and a rebind builds new
    /// channels for the same displays before retiring the old ones -- so when
    /// each channel registered on construction and unregistered on disposal,
    /// the retired channel's unregister cancelled the replacement's
    /// registration, both of them naming the same pair. Apple displays then
    /// silently stopped following changes made anywhere else, from the first
    /// rebind until the plugin was restarted, and a rebind happens on every
    /// wake and every display reconfiguration. Keeping the set here means a
    /// rebind that finds the same displays does nothing at all.
    /// </summary>
    private readonly HashSet<uint> _observedDisplays = [];

    private bool _started;
    private bool _disposed;

    public MacDisplayBrightnessService(Action<string>? log = null)
    {
        _log = log;
        // The callback's second argument is the registration context, which is
        // always this display's own id: see BrightnessChangeCallback.
        _onBrightnessChanged = (_, context, _, _) => PublishExternal(context, known: null);
        _onDisplaysChanged = (_, flags, _) =>
        {
            // Ignore the "about to change" half of each notification.
            const uint BeginConfiguration = 1 << 0;
            if ((flags & BeginConfiguration) == 0)
            {
                Refresh();
            }
        };
    }

    public event Action<string, DisplayBrightnessSnapshot>? Changed;

    public event Action? MonitorsChanged;

    public IReadOnlyList<string> MonitorNames
    {
        get
        {
            lock (_gate)
            {
                return [.. _channels.Select(c => c.Name)];
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        NativeMethods.CGDisplayRegisterReconfigurationCallback(_onDisplaysChanged, IntPtr.Zero);
        Rebind();
    }

    public void Refresh() => Task.Run(Rebind);

    public string? Resolve(string? name)
    {
        lock (_gate)
        {
            if (_channels.Count == 0)
            {
                return null;
            }

            if (string.IsNullOrEmpty(name))
            {
                return _channels[0].Name; // "automatic": the main display
            }

            return _channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;
        }
    }

    public DisplayBrightnessSnapshot Get(string? name)
    {
        lock (_gate)
        {
            var channel = Find(name);
            if (channel is null)
            {
                return string.IsNullOrEmpty(name)
                    ? DisplayBrightnessSnapshot.Unavailable
                    : DisplayBrightnessSnapshot.Unavailable with { Name = $"{name} not connected" };
            }

            var index = _channels.IndexOf(channel);
            return channel.Snapshot with { Index = index, Count = _channels.Count };
        }
    }

    public string? Neighbor(string? name, int direction)
    {
        lock (_gate)
        {
            if (_channels.Count < 2)
            {
                return null;
            }

            var current = Find(name);
            var index = current is null ? -1 : _channels.IndexOf(current);
            var next = ((index + direction) % _channels.Count + _channels.Count) % _channels.Count;
            return _channels[next].Name;
        }
    }

    public bool Adjust(string? name, int delta) =>
        Change(name, s => s with { Level = Math.Clamp(s.Level + delta, 0, 100), Dimmed = false });

    public bool Toggle(string? name) =>
        Change(name, s => s.Dimmed
            ? s with { Dimmed = false, Level = s.Level == 0 ? RestoreFloor : s.Level }
            : s with { Dimmed = true });

    private bool Change(string? name, Func<DisplayBrightnessSnapshot, DisplayBrightnessSnapshot> change)
    {
        Channel? channel;
        DisplayBrightnessSnapshot next;
        int index, count;
        lock (_gate)
        {
            channel = Find(name);
            if (channel is null || !channel.Snapshot.Available)
            {
                return false;
            }

            next = change(channel.Snapshot);
            channel.Snapshot = next;
            index = _channels.IndexOf(channel);
            count = _channels.Count;
        }

        _log?.Invoke($"{channel.Name}: requested {next.Effective}% (level {next.Level}, {(next.Dimmed ? "dimmed" : "on")})");
        channel.Request(next.Effective);
        Changed?.Invoke(channel.Name, next with { Index = index, Count = count });
        return true;
    }

    /// <summary>
    /// Someone else moved a display's brightness. Publish it so the dial matches
    /// what the screen is actually doing. Our own writes also land here; they are
    /// ignored briefly afterwards so a dial being turned is not fought by the
    /// echo of its own change.
    /// </summary>
    /// <param name="displayId">The display that changed.</param>
    /// <param name="known">
    /// The level, when the caller has already read it. Apple panels announce the
    /// change without saying what to, so that path passes null and reads; a DDC
    /// poll has the number in hand and must not pay for a second bus round trip.
    /// </param>
    private void PublishExternal(uint displayId, int? known)
    {
        Channel? channel;
        int index, count;
        lock (_gate)
        {
            channel = _channels.FirstOrDefault(c => c.DisplayId == displayId);
            if (channel is null || channel.WroteRecently || channel.Snapshot.Dimmed)
            {
                return;
            }

            index = _channels.IndexOf(channel);
            count = _channels.Count;
        }

        var level = known ?? channel.ReadPercent();
        if (level is null)
        {
            return;
        }

        DisplayBrightnessSnapshot published;
        lock (_gate)
        {
            if (channel.Snapshot.Level == level.Value)
            {
                return;
            }

            channel.Snapshot = channel.Snapshot with { Level = level.Value };
            published = channel.Snapshot with { Index = index, Count = count };
        }

        _log?.Invoke($"{channel.Name}: changed elsewhere, now {level.Value}%");
        Changed?.Invoke(channel.Name, published);
    }

    /// <summary>
    /// Register the displays that announce their own brightness changes, and
    /// drop the ones that have gone away. The display id doubles as the
    /// registration context, which is what lets the callback say which display
    /// it is about: see <see cref="NativeMethods.BrightnessChangeCallback"/>.
    /// </summary>
    private void SyncBrightnessObservers(List<Channel> channels)
    {
        var wanted = channels.Where(c => c.AnnouncesOwnChanges).Select(c => c.DisplayId).ToHashSet();

        uint[] stale, fresh;
        lock (_gate)
        {
            (fresh, stale) = ObserverChanges(wanted, _observedDisplays);
        }

        foreach (var display in stale)
        {
            NativeMethods.DisplayServicesUnregisterForBrightnessChangeNotifications.Value?.Invoke(display, display);
            lock (_gate)
            {
                _observedDisplays.Remove(display);
            }
        }

        var register = NativeMethods.DisplayServicesRegisterForBrightnessChangeNotifications.Value;
        foreach (var display in fresh)
        {
            if (register?.Invoke(display, display, _onBrightnessChanged) != 0)
            {
                _log?.Invoke($"display {display} refused a brightness-change registration; " +
                             "changes made elsewhere on it will not show up");
                continue;
            }

            lock (_gate)
            {
                _observedDisplays.Add(display);
            }
        }
    }

    /// <summary>
    /// Which registrations to add and which to drop. Pure, because the property
    /// that matters is a negative one: a rebind that finds the same displays
    /// must produce neither, or it cancels its own registrations.
    /// </summary>
    internal static (uint[] Register, uint[] Unregister) ObserverChanges(
        IReadOnlyCollection<uint> wanted, IReadOnlyCollection<uint> observed) =>
        ([.. wanted.Except(observed)], [.. observed.Except(wanted)]);

    private Channel? Find(string? name)
    {
        if (_channels.Count == 0)
        {
            return null;
        }

        return string.IsNullOrEmpty(name)
            ? _channels[0]
            : _channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Re-enumerate displays and re-probe backends. Off the input path: probing talks to hardware.</summary>
    private void Rebind()
    {
        List<Channel> previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = [.. _channels];
        }

        _log?.Invoke("rebinding displays");
        var displays = MacDisplays.Enumerate(_log);
        var built = new List<Channel>();
        var adoptedServices = new HashSet<IntPtr>();

        foreach (var display in displays)
        {
            // D5: probe once, off the input path, and keep whichever protocol answers.
            IMacBrightnessBackend? backend = AppleBrightnessBackend.Supports(display.DisplayId)
                ? new AppleBrightnessBackend(display.DisplayId)
                : DdcBrightnessBackend.Probe(display.AvService, _log);

            if (backend is null)
            {
                _log?.Invoke($"{display.Name}: no brightness channel (neither DisplayServices nor DDC/CI answered)");
                continue;
            }

            var reading = backend.ReadPercent();
            if (reading is null)
            {
                _log?.Invoke($"{display.Name}: {backend.Kind} answered the probe but not the read; skipping");
                continue;
            }

            // Carry the dim state across a rebind so a display waking up does not
            // forget it. The remembered level has to come with it: while dimmed
            // the hardware sits at the dim level, so adopting the reading here
            // would silently replace "restore to 63%" with "restore to 0%".
            var before = previous.FirstOrDefault(c => c.Name == display.Name)?.Snapshot;
            var wasDimmed = before?.Dimmed ?? false;
            var level = wasDimmed && before is not null ? before.Level : reading.Value;
            // The channel adopts the AV service, so it stays alive as long as the
            // backend can be asked to write to it.
            var adopted = backend is DdcBrightnessBackend ? display.AvService : IntPtr.Zero;
            var channel = new Channel(display.Name, display.DisplayId, backend, adopted,
                                      (id, level) => PublishExternal(id, level), _log)
            {
                Snapshot = new DisplayBrightnessSnapshot(display.Name, level, wasDimmed, true),
            };
            adoptedServices.Add(adopted);
            built.Add(channel);

            _log?.Invoke($"{display.Name}: {backend.Kind}, read {reading.Value}%" +
                         $"{(wasDimmed ? $", dimmed (restores to {level}%)" : "")}" +
                         $"{(backend.ReadDisturbsDisplay ? $", polled every {DdcPoll.TotalSeconds:0}s while awake" : ", follows external changes")}");
        }

        List<Channel> retired;
        lock (_gate)
        {
            retired = [.. _channels];
            _channels.Clear();
            _channels.AddRange(built);
        }

        SyncBrightnessObservers(built);

        foreach (var channel in retired)
        {
            channel.Dispose();
        }

        // Anything a channel adopted is released by that channel instead; releasing
        // it here would leave the backend writing through a freed object.
        MacDisplays.Release(displays.Where(d => !adoptedServices.Contains(d.AvService)));
        MonitorsChanged?.Invoke();

        for (var i = 0; i < built.Count; i++)
        {
            Changed?.Invoke(built[i].Name, built[i].Snapshot with { Index = i, Count = built.Count });
        }
    }

    public void Dispose()
    {
        List<Channel> channels;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            channels = [.. _channels];
            _channels.Clear();
        }

        NativeMethods.CGDisplayRemoveReconfigurationCallback(_onDisplaysChanged, IntPtr.Zero);
        SyncBrightnessObservers([]);
        foreach (var channel in channels)
        {
            channel.Dispose();
        }
    }

    /// <summary>
    /// One display's worker. Applies only the latest requested value, so a fast
    /// dial spin costs one or two writes rather than a queue, and periodically
    /// re-reads to notice changes made elsewhere. Private to this display, so a
    /// monitor that stops answering stalls only itself.
    /// </summary>
    private sealed class Channel : IDisposable
    {
        private readonly IMacBrightnessBackend _backend;
        private readonly Action<string>? _log;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _worker;
        private int _requested = -1;
        private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;

        private readonly IntPtr _ownedAvService;
        private readonly uint _displayId;
        private readonly Action<uint, int>? _observed;
        private int _quietPolls;

        public Channel(string name, uint displayId, IMacBrightnessBackend backend, IntPtr ownedAvService,
                       Action<uint, int>? observed, Action<string>? log)
        {
            Name = name;
            _displayId = displayId;
            _backend = backend;
            _ownedAvService = ownedAvService;
            _observed = observed;
            _log = log;
            _worker = Task.Run(RunAsync);
        }

        public string Name { get; }

        public uint DisplayId => _displayId;

        /// <summary>
        /// Whether this display reports brightness changed by anyone. True for
        /// Apple panels; DDC/CI has no such signal, which is what the poll is
        /// for. Registration is the service's to hold, not this channel's.
        /// </summary>
        public bool AnnouncesOwnChanges => !_backend.ReadDisturbsDisplay;

        /// <summary>True just after our own write, so its echo is not mistaken for someone else's change.</summary>
        public bool WroteRecently => DateTimeOffset.UtcNow - _lastWrite < QuietAfterWrite;

        public int? ReadPercent() => _backend.ReadPercent();

        public DisplayBrightnessSnapshot Snapshot { get; set; } = DisplayBrightnessSnapshot.Unavailable;

        public void Request(int percent)
        {
            Interlocked.Exchange(ref _requested, percent);
            if (_wake.CurrentCount == 0)
            {
                try
                {
                    _wake.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Another request beat us to it; the latest value is already queued.
                }
            }
        }

        private TimeSpan IdleInterval => PollInterval(_backend.ReadDisturbsDisplay, _quietPolls);

        private async Task RunAsync()
        {
            var token = _cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _wake.WaitAsync(IdleInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                // A sleeping panel is left alone entirely. On Windows this
                // plugin's periodic DDC read kept monitors awake indefinitely;
                // touching the bus is what does it, so nothing here touches a
                // display that has gone to sleep.
                var asleep = NativeMethods.CGDisplayIsAsleep(_displayId);

                var pending = Interlocked.Exchange(ref _requested, -1);
                if (pending >= 0)
                {
                    if (asleep)
                    {
                        _log?.Invoke($"{Name}: asleep, not applying {pending}%");
                        continue;
                    }

                    if (!_backend.WritePercent(pending))
                    {
                        _log?.Invoke($"{Name}: {_backend.Kind} refused {pending}%");
                    }

                    _lastWrite = DateTimeOffset.UtcNow;
                    continue;
                }

                // Idle: re-read, so a change made anywhere else reaches the
                // strip. A sleeping panel is skipped -- reading a DDC display
                // costs a bus transaction, and doing that on a schedule is what
                // kept monitors from ever staying asleep on Windows. Asleep, its
                // level cannot be changed by anyone either, so there is nothing
                // to miss.
                if (asleep || DateTimeOffset.UtcNow - _lastWrite < QuietAfterWrite)
                {
                    continue;
                }

                var level = _backend.ReadPercent();
                if (level is null)
                {
                    _quietPolls++;
                    continue;
                }

                _quietPolls = 0;
                _observed?.Invoke(_displayId, level.Value);
            }
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            try
            {
                _worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // A worker stuck in a native call cannot be interrupted; let it go.
            }

            _cancellation.Dispose();
            _wake.Dispose();

            // Released only after the worker has stopped, so nothing can write
            // through it afterwards.
            if (_ownedAvService != IntPtr.Zero)
            {
                NativeMethods.CFRelease(_ownedAvService);
            }
        }
    }
}
