using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace NowPlaying.Audio;

/// <summary>
/// <see cref="IVolumeService"/> over NAudio's WASAPI wrappers. Binds to the
/// default multimedia render device, re-binds when Windows changes the
/// default (a short debounce absorbs the burst of role notifications Windows
/// sends), and publishes every volume or mute change, whether it came from
/// the dial, the keyboard, or the tray.
/// </summary>
public sealed class VolumeService : IVolumeService, IMMNotificationClient
{
    private static readonly TimeSpan RebindDebounce = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private string? _deviceId;
    private string _deviceName = "";
    private AudioEndpointVolume? _endpoint;
    private VolumeSnapshot _current = VolumeSnapshot.NoDevice;
    private Timer? _rebindTimer;
    private bool _started;
    private bool _disposed;

    public VolumeService(Action<string>? log = null)
    {
        _log = log;
    }

    public VolumeSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<VolumeSnapshot>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _enumerator = new MMDeviceEnumerator();
            try
            {
                _enumerator.RegisterEndpointNotificationCallback(this);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"device notifications unavailable: {ex.Message}");
            }
        }

        Bind();
    }

    public bool AdjustBy(float delta)
    {
        VolumeSnapshot snapshot;
        lock (_gate)
        {
            if (_endpoint is null)
            {
                return false;
            }

            try
            {
                var level = Math.Clamp(_endpoint.MasterVolumeLevelScalar + delta, 0f, 1f);
                _endpoint.MasterVolumeLevelScalar = level;
                if (_endpoint.Mute)
                {
                    _endpoint.Mute = false;
                }

                snapshot = Read();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"adjust failed: {ex.Message}");
                return false;
            }

            if (snapshot == _current)
            {
                return true;
            }

            _current = snapshot;
        }

        Changed?.Invoke(snapshot);
        return true;
    }

    public bool SetLevel(float level)
    {
        VolumeSnapshot snapshot;
        lock (_gate)
        {
            if (_endpoint is null)
            {
                return false;
            }

            try
            {
                _endpoint.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
                snapshot = Read();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"set level failed: {ex.Message}");
                return false;
            }

            if (snapshot == _current)
            {
                return true;
            }

            _current = snapshot;
        }

        Changed?.Invoke(snapshot);
        return true;
    }

    public bool ToggleMute()
    {
        VolumeSnapshot snapshot;
        lock (_gate)
        {
            if (_endpoint is null)
            {
                return false;
            }

            try
            {
                _endpoint.Mute = !_endpoint.Mute;
                snapshot = Read();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"mute failed: {ex.Message}");
                return false;
            }

            if (snapshot == _current)
            {
                return true;
            }

            _current = snapshot;
        }

        Changed?.Invoke(snapshot);
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
            _rebindTimer?.Dispose();
            _rebindTimer = null;
            Unbind();
            if (_enumerator is not null)
            {
                try
                {
                    _enumerator.UnregisterEndpointNotificationCallback(this);
                }
                catch
                {
                }

                _enumerator.Dispose();
                _enumerator = null;
            }
        }
    }

    // -- binding ------------------------------------------------------------

    private void Bind()
    {
        VolumeSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed || _enumerator is null)
            {
                return;
            }

            Unbind();
            try
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceId = _device.ID;
                _deviceName = _device.FriendlyName;
                _endpoint = _device.AudioEndpointVolume;
                _endpoint.OnVolumeNotification += OnVolumeNotification;
                snapshot = Read();
                _log?.Invoke($"bound to {_deviceName}: {snapshot.Percent}%{(snapshot.Muted ? " muted" : "")}");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"no default output device: {ex.Message}");
                Unbind();
                snapshot = VolumeSnapshot.NoDevice;
            }

            if (snapshot == _current)
            {
                return;
            }

            _current = snapshot;
        }

        Changed?.Invoke(snapshot);
    }

    /// <summary>Caller holds the gate.</summary>
    private void Unbind()
    {
        if (_endpoint is not null)
        {
            try
            {
                _endpoint.OnVolumeNotification -= OnVolumeNotification;
            }
            catch
            {
            }
        }

        _endpoint = null;
        _device?.Dispose();
        _device = null;
        _deviceId = null;
        _deviceName = "";
    }

    /// <summary>Caller holds the gate.</summary>
    private VolumeSnapshot Read() => _endpoint is null
        ? VolumeSnapshot.NoDevice
        : new VolumeSnapshot(_deviceName, _endpoint.MasterVolumeLevelScalar, _endpoint.Mute, true);

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        VolumeSnapshot snapshot;
        lock (_gate)
        {
            if (_endpoint is null)
            {
                return;
            }

            snapshot = new VolumeSnapshot(_deviceName, data.MasterVolume, data.Muted, true);
            if (snapshot == _current)
            {
                return;
            }

            _current = snapshot;
        }

        Changed?.Invoke(snapshot);
    }

    private void ScheduleRebind(string reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _log?.Invoke($"rebind scheduled: {reason}");
            _rebindTimer?.Dispose();
            _rebindTimer = new Timer(_ => Bind(), null, RebindDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    // -- IMMNotificationClient ----------------------------------------------

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
        {
            ScheduleRebind($"default device changed to {defaultDeviceId}");
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (deviceId == _deviceId && newState != DeviceState.Active)
        {
            ScheduleRebind($"device state {newState}");
        }
    }

    void IMMNotificationClient.OnDeviceRemoved(string deviceId)
    {
        if (deviceId == _deviceId)
        {
            ScheduleRebind("device removed");
        }
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
    {
        // A new device only matters if Windows makes it the default, which arrives as OnDefaultDeviceChanged.
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
    }
}
