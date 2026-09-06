namespace NowPlaying.Device;

/// <summary>The primary monitor's brightness as the plugin knows it.</summary>
/// <param name="Name">The monitor's name from its EDID, or the driver's description.</param>
/// <param name="Level">Brightness as a percent of the monitor's own range; the level to restore when dimmed.</param>
/// <param name="Dimmed">Whether the toggle has taken the monitor to its minimum without forgetting the level.</param>
/// <param name="Available">False when no monitor answers DDC/CI.</param>
public sealed record DisplayBrightnessSnapshot(string Name, int Level, bool Dimmed, bool Available)
{
    public static DisplayBrightnessSnapshot Unavailable { get; } = new("No DDC/CI monitor", 0, false, false);

    /// <summary>What is sent to the monitor, as a percent.</summary>
    public int Effective => Dimmed ? 0 : Level;
}

/// <summary>
/// Brightness of the primary monitor over DDC/CI. A dedicated worker applies
/// only the latest requested value, so a fast spin costs one or two slow
/// round trips instead of a queue of them; a periodic re-read picks up
/// changes made on the monitor's own menu, which sends no notification.
/// </summary>
public sealed class DisplayBrightnessService : IDisposable
{
    /// <summary>Restore level when un-dimming from a level of 0.</summary>
    public const int RestoreFloor = 30;

    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private PhysicalMonitor? _monitor;
    private uint _min;
    private uint _max;
    private DisplayBrightnessSnapshot _current = DisplayBrightnessSnapshot.Unavailable;
    private int _pendingPercent = -1;
    private bool _rebindRequested;
    private Timer? _readTimer;
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

    /// <summary>Raised on the worker or timer thread whenever the snapshot changes.</summary>
    public event Action<DisplayBrightnessSnapshot>? Changed;

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
        }

        _wake.Set();
    }

    // -- worker -------------------------------------------------------------

    private void RequestRead()
    {
        lock (_gate)
        {
            if (_pendingPercent >= 0)
            {
                return; // a write is on its way; the read after it will be fresh
            }
        }

        _wake.Set();
    }

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

            if (rebind || _monitor is null)
            {
                Bind();
            }

            // Apply the latest requested value, repeating only if another arrived meanwhile.
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

                Write(percent);
            }

            if (!rebind)
            {
                ReadAndPublish();
            }
        }

        ReleaseMonitor();
    }

    private void Bind()
    {
        ReleaseMonitor();

        IReadOnlyList<PhysicalMonitor> monitors;
        try
        {
            monitors = MonitorConfiguration.Enumerate(_log);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"monitor enumeration failed: {ex.Message}");
            Publish(DisplayBrightnessSnapshot.Unavailable);
            return;
        }

        var chosen = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        MonitorConfiguration.Destroy(monitors.Where(m => m != chosen));
        if (chosen is null)
        {
            _log?.Invoke("no monitors");
            Publish(DisplayBrightnessSnapshot.Unavailable);
            return;
        }

        try
        {
            var (min, current, max) = MonitorConfiguration.ReadBrightness(chosen.Handle);
            _monitor = chosen;
            _min = min;
            _max = max;
            var percent = MonitorConfiguration.ToPercent(min, current, max);
            _log?.Invoke($"bound to {chosen.Name} ({chosen.GdiDeviceName}): {current} of {min}..{max} = {percent}%");
            Publish(new DisplayBrightnessSnapshot(chosen.Name, percent, false, true));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"{chosen.Name} does not answer DDC/CI: {ex.Message}");
            MonitorConfiguration.Destroy([chosen]);
            Publish(DisplayBrightnessSnapshot.Unavailable);
        }
    }

    private void Write(int percent)
    {
        var monitor = _monitor;
        if (monitor is null)
        {
            return;
        }

        try
        {
            MonitorConfiguration.WriteBrightness(monitor.Handle, MonitorConfiguration.ToUnits(_min, _max, percent));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"write {percent}% failed: {ex.Message}");
            lock (_gate)
            {
                _rebindRequested = true;
            }

            _wake.Set();
        }
    }

    /// <summary>Periodic read: adopt a level changed on the monitor's own menu, and drop the dim state if someone raised it there.</summary>
    private void ReadAndPublish()
    {
        var monitor = _monitor;
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
                if (_pendingPercent >= 0 || percent == _current.Effective)
                {
                    return;
                }

                next = _current with { Level = percent, Dimmed = false };
                _current = next;
            }

            _log?.Invoke($"monitor reports {percent}% (changed elsewhere)");
            Changed?.Invoke(next);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"read failed: {ex.Message}");
            lock (_gate)
            {
                _rebindRequested = true;
            }

            _wake.Set();
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

    private void ReleaseMonitor()
    {
        var monitor = _monitor;
        _monitor = null;
        if (monitor is not null)
        {
            MonitorConfiguration.Destroy([monitor]);
        }
    }
}
