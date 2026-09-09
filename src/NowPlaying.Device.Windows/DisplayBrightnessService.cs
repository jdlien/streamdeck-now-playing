namespace NowPlaying.Device;

/// <summary>
/// Brightness of every monitor that answers DDC/CI, addressed by name. Each
/// dial binds to a monitor (or to "automatic", the primary) and reads and
/// writes its own; the service drives them all from one worker.
///
/// DDC/CI is slow and moody: a transaction takes tens of milliseconds, the
/// monitor may not acknowledge for a while after a write, and Windows
/// invalidates handles on display changes. So the worker applies only the
/// latest requested value per monitor (a fast spin costs one or two writes,
/// not a queue), reads never follow a write directly, and failures schedule
/// a re-bind with backoff rather than an immediate retry.
/// </summary>
public sealed class DisplayBrightnessService : IDisplayBrightnessService
{
    /// <summary>Restore level when un-dimming from a level of 0.</summary>
    public const int RestoreFloor = 30;

    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// No reads this soon after a write: the Odyssey G95NC refused reads for a
    /// couple of seconds after a write and once returned the old value, which
    /// the periodic re-read would have adopted as a change made elsewhere.
    /// </summary>
    private static readonly TimeSpan QuietAfterWrite = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstRebindDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRebindDelay = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private readonly List<Channel> _channels = new(); // primary first
    private bool _rebindRequested;
    private bool _readRequested;
    private int _failures;
    private Timer? _readTimer;
    private Timer? _rebindTimer;
    private bool _disposed;

    public DisplayBrightnessService(Action<string>? log = null)
    {
        _log = log;
        _worker = new Thread(Run) { IsBackground = true, Name = "display-brightness" };
    }

    /// <summary>Raised on the worker thread when one monitor's snapshot changes.</summary>
    public event Action<string, DisplayBrightnessSnapshot>? Changed;

    /// <summary>Raised on the worker thread when the set of monitors changed (after every bind).</summary>
    public event Action? MonitorsChanged;

    /// <summary>Names of the monitors that answer DDC/CI, primary first.</summary>
    public IReadOnlyList<string> MonitorNames
    {
        get
        {
            lock (_gate)
            {
                return _channels.Select(c => c.State.Name).ToArray();
            }
        }
    }

    /// <summary>The actual monitor a binding refers to: null or empty means the primary. Null when nothing matches.</summary>
    public string? Resolve(string? name)
    {
        lock (_gate)
        {
            return Find(name)?.State.Name;
        }
    }

    /// <summary>The snapshot for a binding; an unavailable one when the monitor is absent.</summary>
    public DisplayBrightnessSnapshot Get(string? name)
    {
        lock (_gate)
        {
            var channel = Find(name);
            if (channel is not null)
            {
                return channel.State;
            }

            return string.IsNullOrEmpty(name)
                ? DisplayBrightnessSnapshot.Unavailable
                : DisplayBrightnessSnapshot.Unavailable with { Name = $"{name} not connected" };
        }
    }

    /// <summary>The monitor after (+1) or before (-1) the given binding in the list, wrapping. Null when there is nothing to move to.</summary>
    public string? Neighbor(string? name, int direction)
    {
        lock (_gate)
        {
            if (_channels.Count < 2)
            {
                return null;
            }

            var index = Math.Max(0, _channels.FindIndex(c => c == Find(name)));
            var count = _channels.Count;
            return _channels[((index + direction) % count + count) % count].State.Name;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_worker.IsAlive || _disposed)
            {
                return;
            }

            _rebindRequested = true;
            _worker.Start();
            _readTimer = new Timer(_ => RequestRead(), null, ReadInterval, ReadInterval);
        }

        _wake.Set();
    }

    /// <summary>Re-enumerate monitors and re-read, e.g. after wake or a display change.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _rebindRequested = true;
        }

        _wake.Set();
    }

    /// <summary>Move a monitor's level by <paramref name="delta"/> percent, un-dimming if dimmed. False when it is not available.</summary>
    public bool Adjust(string? name, int delta) => Change(name, s => s with { Level = Math.Clamp(s.Level + delta, 0, 100), Dimmed = false });

    /// <summary>Toggle a monitor between its minimum and the remembered level.</summary>
    public bool Toggle(string? name) => Change(name, s => s.Dimmed
        ? s with { Level = s.Level == 0 ? RestoreFloor : s.Level, Dimmed = false }
        : s with { Dimmed = true });

    private bool Change(string? name, Func<DisplayBrightnessSnapshot, DisplayBrightnessSnapshot> change)
    {
        DisplayBrightnessSnapshot next;
        lock (_gate)
        {
            var channel = Find(name);
            if (channel is null)
            {
                return false;
            }

            next = change(channel.State);
            channel.State = next;
            channel.Pending = next.Effective;
        }

        Changed?.Invoke(next.Name, next);
        _wake.Set();
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _readTimer?.Dispose();
            _readTimer = null;
            _rebindTimer?.Dispose();
            _rebindTimer = null;
        }

        _wake.Set();
    }

    /// <summary>Caller holds the gate. Empty or null means the primary.</summary>
    private Channel? Find(string? name)
    {
        if (_channels.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(name))
        {
            return _channels[0];
        }

        return _channels.FirstOrDefault(c => string.Equals(c.State.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // -- scheduling ---------------------------------------------------------

    private void RequestRead()
    {
        lock (_gate)
        {
            _readRequested = true;
        }

        _wake.Set();
    }

    /// <summary>After a failure: re-bind later, with the delay growing while failures continue.</summary>
    private void ScheduleRebind(string reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var delay = TimeSpan.FromTicks(Math.Min(FirstRebindDelay.Ticks << Math.Min(_failures, 4), MaxRebindDelay.Ticks));
            _failures++;
            _log?.Invoke($"{reason}; re-binding in {delay.TotalSeconds:0}s");
            _rebindTimer?.Dispose();
            _rebindTimer = new Timer(
                _ =>
                {
                    lock (_gate)
                    {
                        _rebindRequested = true;
                    }

                    _wake.Set();
                },
                null,
                delay,
                Timeout.InfiniteTimeSpan);
        }
    }

    // -- worker -------------------------------------------------------------

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();

            bool rebind;
            lock (_gate)
            {
                if (_disposed)
                {
                    break;
                }

                rebind = _rebindRequested;
                _rebindRequested = false;
            }

            if (rebind)
            {
                Bind();
            }

            // Apply the latest requested value on every monitor that has one.
            var wroteAny = false;
            foreach (var channel in Snapshot())
            {
                while (true)
                {
                    int percent;
                    lock (_gate)
                    {
                        percent = channel.Pending;
                        channel.Pending = -1;
                    }

                    if (percent < 0)
                    {
                        break;
                    }

                    wroteAny |= Write(channel, percent);
                }
            }

            // Reads only when the timer asked, and never on the heels of a write to that monitor.
            bool read;
            lock (_gate)
            {
                read = _readRequested && !rebind;
                if (read || wroteAny || rebind)
                {
                    _readRequested = false;
                }
            }

            if (read)
            {
                foreach (var channel in Snapshot())
                {
                    bool quiet;
                    lock (_gate)
                    {
                        quiet = channel.Pending < 0 && DateTime.UtcNow - channel.LastWriteUtc >= QuietAfterWrite;
                    }

                    if (quiet)
                    {
                        ReadAndPublish(channel);
                    }
                }
            }
        }

        ReleaseAll();
    }

    private List<Channel> Snapshot()
    {
        lock (_gate)
        {
            return _channels.ToList();
        }
    }

    private void Bind()
    {
        // Enumerate before releasing the old handles: destroying a physical
        // monitor handle and immediately reopening the same monitor is the
        // sequence that makes the first DDC/CI read fail.
        IReadOnlyList<PhysicalMonitor> found;
        try
        {
            found = MonitorConfiguration.Enumerate(_log);
        }
        catch (Exception ex)
        {
            ReleaseAll();
            MonitorsChanged?.Invoke();
            ScheduleRebind($"monitor enumeration failed: {ex.Message}");
            return;
        }

        var previous = ReleaseAll();

        var channels = new List<Channel>();
        foreach (var monitor in found)
        {
            try
            {
                var (min, current, max) = MonitorConfiguration.ReadBrightness(monitor.Handle);
                var percent = BrightnessMath.ToPercent(min, current, max);
                var old = previous.FirstOrDefault(c => string.Equals(c.State.Name, monitor.Name, StringComparison.OrdinalIgnoreCase));
                // A monitor we had dimmed still reads as its minimum after a re-bind; keep the remembered level.
                var state = old is { State.Dimmed: true } && percent == 0
                    ? old.State
                    : new DisplayBrightnessSnapshot(monitor.Name, percent, false, true);
                channels.Add(new Channel(monitor) { Min = min, Max = max, State = state });
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{monitor.Name} ({monitor.GdiDeviceName}) does not answer DDC/CI: {ex.Message}");
                MonitorConfiguration.Destroy([monitor]);
            }
        }

        for (var i = 0; i < channels.Count; i++)
        {
            channels[i].State = channels[i].State with { Index = i, Count = channels.Count };
        }

        lock (_gate)
        {
            _channels.Clear();
            _channels.AddRange(channels);
            if (channels.Count > 0)
            {
                _failures = 0;
            }
        }

        _log?.Invoke(channels.Count == 0
            ? "no monitor answers DDC/CI"
            : $"{channels.Count} monitor(s) answer DDC/CI: {string.Join(", ", channels.Select(c => $"{c.State.Name} {c.State.Level}%"))}");

        MonitorsChanged?.Invoke();
        foreach (var channel in channels)
        {
            Changed?.Invoke(channel.State.Name, channel.State);
        }

        if (channels.Count == 0)
        {
            ScheduleRebind("no monitor answers DDC/CI");
        }
    }

    /// <summary>True when the value reached the monitor.</summary>
    private bool Write(Channel channel, int percent)
    {
        try
        {
            MonitorConfiguration.WriteBrightness(channel.Monitor.Handle, BrightnessMath.ToUnits(channel.Min, channel.Max, percent));
            lock (_gate)
            {
                channel.LastWriteUtc = DateTime.UtcNow;
                _failures = 0;
            }

            return true;
        }
        catch (Exception ex)
        {
            ScheduleRebind($"write {percent}% to {channel.State.Name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Periodic read: adopt a level changed on the monitor's own menu, and drop the dim state if someone raised it there.</summary>
    private void ReadAndPublish(Channel channel)
    {
        try
        {
            var (min, current, max) = MonitorConfiguration.ReadBrightness(channel.Monitor.Handle);
            var percent = BrightnessMath.ToPercent(min, current, max);

            DisplayBrightnessSnapshot next;
            lock (_gate)
            {
                channel.Min = min;
                channel.Max = max;
                _failures = 0;
                if (channel.Pending >= 0 || percent == channel.State.Effective)
                {
                    return;
                }

                next = channel.State with { Level = percent, Dimmed = false };
                channel.State = next;
            }

            _log?.Invoke($"{next.Name} reports {percent}% (changed elsewhere)");
            Changed?.Invoke(next.Name, next);
        }
        catch (Exception ex)
        {
            ScheduleRebind($"read from {channel.State.Name} failed: {ex.Message}");
        }
    }

    /// <summary>Drop every channel and release its handle; returns what was dropped so state can be carried over.</summary>
    private List<Channel> ReleaseAll()
    {
        List<Channel> channels;
        lock (_gate)
        {
            channels = _channels.ToList();
            _channels.Clear();
        }

        MonitorConfiguration.Destroy(channels.Select(c => c.Monitor));
        return channels;
    }

    private sealed class Channel(PhysicalMonitor monitor)
    {
        public PhysicalMonitor Monitor { get; } = monitor;
        public uint Min;
        public uint Max;
        public DisplayBrightnessSnapshot State = DisplayBrightnessSnapshot.Unavailable;
        public int Pending = -1;
        public DateTime LastWriteUtc = DateTime.MinValue;
    }
}
