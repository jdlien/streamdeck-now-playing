namespace NowPlaying.Device;

/// <summary>The selected monitor's brightness as the plugin knows it.</summary>
/// <param name="Name">The monitor's name from its EDID, or the driver's description.</param>
/// <param name="Level">Brightness as a percent of the monitor's own range; the level to restore when dimmed.</param>
/// <param name="Dimmed">Whether the toggle has taken the monitor to its minimum without forgetting the level.</param>
/// <param name="Available">False when no monitor answers DDC/CI.</param>
/// <param name="Index">Position of the selected monitor among those that answer, 0-based.</param>
/// <param name="Count">How many monitors answer DDC/CI.</param>
public sealed record DisplayBrightnessSnapshot(string Name, int Level, bool Dimmed, bool Available, int Index = 0, int Count = 0)
{
    public static DisplayBrightnessSnapshot Unavailable { get; } = new("No DDC/CI monitor", 0, false, false);

    /// <summary>What is sent to the monitor, as a percent.</summary>
    public int Effective => Dimmed ? 0 : Level;
}

/// <summary>
/// Brightness of one monitor over DDC/CI, with the choice of monitor.
///
/// DDC/CI is slow and moody: a transaction takes tens of milliseconds, the
/// monitor may not acknowledge for a while after a write, and Windows
/// invalidates handles on display changes. So a dedicated worker applies only
/// the latest requested value (a fast spin costs one or two writes, not a
/// queue), reads never follow a write directly, and failures schedule a
/// re-bind with backoff rather than an immediate retry.
/// </summary>
public sealed class DisplayBrightnessService : IDisposable
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
    private readonly List<PhysicalMonitor> _monitors = new();
    private int _index = -1;
    private uint _min;
    private uint _max;
    private string? _preferredName;
    private DisplayBrightnessSnapshot _current = DisplayBrightnessSnapshot.Unavailable;
    private int _pendingPercent = -1;
    private int _pendingSelect = -1;
    private bool _rebindRequested;
    private bool _readRequested;
    private int _failures;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private Timer? _readTimer;
    private Timer? _rebindTimer;
    private bool _disposed;

    public DisplayBrightnessService(Action<string>? log = null)
    {
        _log = log;
        _worker = new Thread(Run) { IsBackground = true, Name = "display-brightness" };
    }

    public DisplayBrightnessSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised on the worker thread whenever the snapshot changes.</summary>
    public event Action<DisplayBrightnessSnapshot>? Changed;

    /// <summary>Names of the monitors that answered DDC/CI at the last bind, in selection order.</summary>
    public IReadOnlyList<string> MonitorNames
    {
        get
        {
            lock (_gate)
            {
                return _monitors.Select(m => m.Name).ToArray();
            }
        }
    }

    /// <summary>The monitor to select at bind when present; otherwise the primary. Null clears the preference.</summary>
    public void SetPreferredMonitor(string? name)
    {
        lock (_gate)
        {
            _preferredName = string.IsNullOrWhiteSpace(name) ? null : name;
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

    /// <summary>Move the level by <paramref name="delta"/> percent, un-dimming if dimmed. False when no monitor is available.</summary>
    public bool Adjust(int delta)
    {
        DisplayBrightnessSnapshot next;
        lock (_gate)
        {
            if (!_current.Available)
            {
                return false;
            }

            next = _current with { Level = Math.Clamp(_current.Level + delta, 0, 100), Dimmed = false };
            _current = next;
            _pendingPercent = next.Effective;
        }

        Changed?.Invoke(next);
        _wake.Set();
        return true;
    }

    /// <summary>Toggle between the monitor's minimum and the remembered level.</summary>
    public bool Toggle()
    {
        DisplayBrightnessSnapshot next;
        lock (_gate)
        {
            if (!_current.Available)
            {
                return false;
            }

            next = _current.Dimmed
                ? _current with { Level = _current.Level == 0 ? RestoreFloor : _current.Level, Dimmed = false }
                : _current with { Dimmed = true };
            _current = next;
            _pendingPercent = next.Effective;
        }

        Changed?.Invoke(next);
        _wake.Set();
        return true;
    }

    /// <summary>
    /// Select the next monitor that answers DDC/CI, wrapping around. False
    /// when there is nothing to cycle to. The read of the new monitor happens
    /// on the worker; the snapshot changes when it lands.
    /// </summary>
    public bool NextMonitor()
    {
        lock (_gate)
        {
            if (_monitors.Count < 2 || _index < 0)
            {
                return false;
            }

            _pendingSelect = (_index + 1) % _monitors.Count;
            _pendingPercent = -1; // a level requested for the old monitor is not for the new one
        }

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
            int select;
            lock (_gate)
            {
                if (_disposed)
                {
                    break;
                }

                rebind = _rebindRequested;
                _rebindRequested = false;
                select = _pendingSelect;
                _pendingSelect = -1;
            }

            if (rebind || _index < 0)
            {
                Bind();
            }
            else if (select >= 0)
            {
                Select(select);
            }

            // Apply the latest requested value, repeating only if another arrived meanwhile.
            var wrote = false;
            while (true)
            {
                int percent;
                lock (_gate)
                {
                    percent = _pendingPercent;
                    _pendingPercent = -1;
                }

                if (percent < 0)
                {
                    break;
                }

                wrote |= Write(percent);
            }

            // Reads only when the timer asked, never on the heels of a write.
            bool read;
            lock (_gate)
            {
                read = _readRequested && !wrote && !rebind && select < 0 && _pendingPercent < 0
                    && DateTime.UtcNow - _lastWriteUtc >= QuietAfterWrite;
                if (read || wrote || rebind)
                {
                    _readRequested = false;
                }
            }

            if (read)
            {
                ReadAndPublish();
            }
        }

        ReleaseMonitors();
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
            ReleaseMonitors();
            Publish(DisplayBrightnessSnapshot.Unavailable);
            ScheduleRebind($"monitor enumeration failed: {ex.Message}");
            return;
        }

        ReleaseMonitors();

        // Keep only monitors that answer DDC/CI; the others' handles go straight back.
        var answering = new List<PhysicalMonitor>();
        foreach (var monitor in found)
        {
            try
            {
                MonitorConfiguration.ReadBrightness(monitor.Handle);
                answering.Add(monitor);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"{monitor.Name} ({monitor.GdiDeviceName}) does not answer DDC/CI: {ex.Message}");
                MonitorConfiguration.Destroy([monitor]);
            }
        }

        string? preferred;
        lock (_gate)
        {
            _monitors.Clear();
            _monitors.AddRange(answering);
            preferred = _preferredName;
        }

        if (answering.Count == 0)
        {
            Publish(DisplayBrightnessSnapshot.Unavailable);
            ScheduleRebind("no monitor answers DDC/CI");
            return;
        }

        var index = preferred is null ? -1 : answering.FindIndex(m => string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = Math.Max(0, answering.FindIndex(m => m.IsPrimary));
        }

        _log?.Invoke($"{answering.Count} monitor(s) answer DDC/CI: {string.Join(", ", answering.Select(m => m.Name))}");
        Select(index);
    }

    /// <summary>Make monitor <paramref name="index"/> current and publish its brightness. Worker thread only.</summary>
    private void Select(int index)
    {
        PhysicalMonitor monitor;
        int count;
        lock (_gate)
        {
            if (index < 0 || index >= _monitors.Count)
            {
                return;
            }

            monitor = _monitors[index];
            count = _monitors.Count;
        }

        try
        {
            var (min, current, max) = MonitorConfiguration.ReadBrightness(monitor.Handle);
            _min = min;
            _max = max;
            _index = index;
            lock (_gate)
            {
                _failures = 0;
            }

            var percent = MonitorConfiguration.ToPercent(min, current, max);
            _log?.Invoke($"selected {monitor.Name} ({monitor.GdiDeviceName}): {current} of {min}..{max} = {percent}%");
            Publish(new DisplayBrightnessSnapshot(monitor.Name, percent, false, true, index, count));
        }
        catch (Exception ex)
        {
            ScheduleRebind($"{monitor.Name} stopped answering: {ex.Message}");
        }
    }

    private PhysicalMonitor? Selected()
    {
        lock (_gate)
        {
            return _index >= 0 && _index < _monitors.Count ? _monitors[_index] : null;
        }
    }

    /// <summary>True when the value reached the monitor.</summary>
    private bool Write(int percent)
    {
        var monitor = Selected();
        if (monitor is null)
        {
            return false;
        }

        try
        {
            MonitorConfiguration.WriteBrightness(monitor.Handle, MonitorConfiguration.ToUnits(_min, _max, percent));
            lock (_gate)
            {
                _lastWriteUtc = DateTime.UtcNow;
                _failures = 0;
            }

            return true;
        }
        catch (Exception ex)
        {
            ScheduleRebind($"write {percent}% to {monitor.Name} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Periodic read: adopt a level changed on the monitor's own menu, and drop the dim state if someone raised it there.</summary>
    private void ReadAndPublish()
    {
        var monitor = Selected();
        if (monitor is null)
        {
            return;
        }

        try
        {
            var (min, current, max) = MonitorConfiguration.ReadBrightness(monitor.Handle);
            _min = min;
            _max = max;
            var percent = MonitorConfiguration.ToPercent(min, current, max);

            DisplayBrightnessSnapshot next;
            lock (_gate)
            {
                _failures = 0;
                if (_pendingPercent >= 0 || _pendingSelect >= 0 || percent == _current.Effective)
                {
                    return;
                }

                next = _current with { Level = percent, Dimmed = false };
                _current = next;
            }

            _log?.Invoke($"{monitor.Name} reports {percent}% (changed elsewhere)");
            Changed?.Invoke(next);
        }
        catch (Exception ex)
        {
            ScheduleRebind($"read from {monitor.Name} failed: {ex.Message}");
        }
    }

    private void Publish(DisplayBrightnessSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot == _current)
            {
                return;
            }

            _current = snapshot;
        }

        Changed?.Invoke(snapshot);
    }

    private void ReleaseMonitors()
    {
        List<PhysicalMonitor> monitors;
        lock (_gate)
        {
            monitors = _monitors.ToList();
            _monitors.Clear();
            _index = -1;
        }

        MonitorConfiguration.Destroy(monitors);
    }
}
