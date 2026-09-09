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
    /// How often a display whose reads are free is re-read, to notice
    /// brightness changed elsewhere. Displays whose reads cost an I2C round
    /// trip are never polled: see <see cref="Channel.RunAsync"/>.
    /// </summary>
    private static readonly TimeSpan IdleWake = TimeSpan.FromSeconds(30);

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

    private bool _started;
    private bool _disposed;

    public MacDisplayBrightnessService(Action<string>? log = null)
    {
        _log = log;
        _onBrightnessChanged = (_, display, _, _) => ExternalBrightnessChanged(display);
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
    /// Someone else moved an Apple display's brightness. Re-read and publish so
    /// the dial matches what the screen is actually doing. Our own writes also
    /// land here; they are ignored briefly afterwards so a dial being turned is
    /// not fought by the echo of its own change.
    /// </summary>
    private void ExternalBrightnessChanged(uint displayId)
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

        var level = channel.ReadPercent();
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

        Changed?.Invoke(channel.Name, published);
    }

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
            var channel = new Channel(display.Name, display.DisplayId, backend, adopted, _log)
            {
                Snapshot = new DisplayBrightnessSnapshot(display.Name, level, wasDimmed, true),
            };
            adoptedServices.Add(adopted);
            built.Add(channel);
            // Apple panels report brightness changes made by anyone -- the
            // keyboard keys, System Settings, another utility -- so the strip
            // can follow them. DDC monitors have no equivalent signal, which is
            // why a change made elsewhere on those is not noticed until a rebind.
            if (!backend.ReadDisturbsDisplay)
            {
                var register = NativeMethods.DisplayServicesRegisterForBrightnessChangeNotifications.Value;
                if (register?.Invoke(display.DisplayId, display.DisplayId, _onBrightnessChanged) == 0)
                {
                    channel.ObservesExternalChanges = true;
                }
            }

            _log?.Invoke($"{display.Name}: {backend.Kind}, read {reading.Value}%{(wasDimmed ? $", dimmed (restores to {level}%)" : "")}");
        }

        List<Channel> retired;
        lock (_gate)
        {
            retired = [.. _channels];
            _channels.Clear();
            _channels.AddRange(built);
        }

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

        public Channel(string name, uint displayId, IMacBrightnessBackend backend, IntPtr ownedAvService, Action<string>? log)
        {
            Name = name;
            _displayId = displayId;
            _backend = backend;
            _ownedAvService = ownedAvService;
            _log = log;
            _worker = Task.Run(RunAsync);
        }

        public string Name { get; }

        public uint DisplayId => _displayId;

        /// <summary>Set once DisplayServices accepted a brightness-change registration for this display.</summary>
        public bool ObservesExternalChanges { get; set; }

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

        private async Task RunAsync()
        {
            var token = _cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _wake.WaitAsync(IdleWake, token).ConfigureAwait(false);
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

                // Idle. Polling a display whose reads cost a bus transaction is
                // what stops a monitor ever staying asleep, so those are never
                // polled: their level is re-read on rebind, which a display
                // reconfiguration or a wake already triggers.
                if (_backend.ReadDisturbsDisplay || asleep)
                {
                    continue;
                }

                if (DateTimeOffset.UtcNow - _lastWrite < QuietAfterWrite)
                {
                    continue;
                }

                _ = _backend.ReadPercent();
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

            if (ObservesExternalChanges)
            {
                NativeMethods.DisplayServicesUnregisterForBrightnessChangeNotifications.Value?.Invoke(_displayId, _displayId);
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
