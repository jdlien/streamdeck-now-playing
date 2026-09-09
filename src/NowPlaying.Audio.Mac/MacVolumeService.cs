using System.Runtime.Versioning;

namespace NowPlaying.Audio;

/// <summary>
/// <see cref="IVolumeService"/> over CoreAudio. Binds to the default output
/// device, tracks its volume and mute through property listeners, and rebinds
/// when the default device changes.
///
/// The awkward part of macOS, and the reason <see cref="VolumeSnapshot"/> grew
/// capability flags: WASAPI guarantees every endpoint a software volume,
/// CoreAudio does not. Measured on the target machine, the Studio Display
/// speakers and the MacBook speakers expose a virtual main volume, while an
/// SSL 2 MkII and a BenQ monitor expose none at all -- their gain is a physical
/// knob. Those devices are reported as present but not settable rather than
/// pretending at a level (macos-port-plan D4).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacVolumeService : IVolumeService
{
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    // Held in fields so the marshalled thunks outlive the native registration:
    // a listener collected while CoreAudio still holds it takes the process down.
    private readonly CoreAudio.ListenerProc _onDefaultDeviceChanged;
    private readonly CoreAudio.ListenerProc _onDevicePropertyChanged;

    private static readonly CoreAudio.PropertyAddress DefaultDeviceAddress =
        new(CoreAudio.DefaultOutputDevice, CoreAudio.ScopeGlobal, CoreAudio.ElementMain);

    private static readonly CoreAudio.PropertyAddress VolumeAddress =
        new(CoreAudio.VirtualMainVolume, CoreAudio.ScopeOutput, CoreAudio.ElementMain);

    private static readonly CoreAudio.PropertyAddress MuteAddress =
        new(CoreAudio.Mute, CoreAudio.ScopeOutput, CoreAudio.ElementMain);

    private static readonly CoreAudio.PropertyAddress NameAddress =
        new(CoreAudio.ObjectName, CoreAudio.ScopeGlobal, CoreAudio.ElementMain);

    private uint _device;
    private bool _started;
    private bool _disposed;
    private VolumeSnapshot _current = VolumeSnapshot.NoDevice;

    /// <summary>
    /// Bumped on every rebind. A listener callback that fires after the default
    /// device changed carries the old generation and is dropped, so a late
    /// callback from the previous device cannot overwrite the new snapshot.
    /// </summary>
    private int _generation;

    public MacVolumeService(Action<string>? log = null)
    {
        _log = log;
        _onDefaultDeviceChanged = (_, _, _, _) => { Rebind(); return 0; };
        _onDevicePropertyChanged = (objectId, _, _, _) => { OnDeviceChanged(objectId); return 0; };
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
        }

        var status = CoreAudio.AudioObjectAddPropertyListener(
            CoreAudio.SystemObject, DefaultDeviceAddress, _onDefaultDeviceChanged, IntPtr.Zero);
        if (status != 0)
        {
            // Not fatal: the dial still works, it just will not follow a device switch.
            _log?.Invoke($"default-device listener failed ({status}); the dial will not follow device changes");
        }

        Rebind();
    }

    public bool AdjustBy(float delta)
    {
        var current = Current;
        return current.CanSetVolume && SetLevel(Math.Clamp(current.Level + delta, 0f, 1f));
    }

    public bool SetLevel(float level)
    {
        uint device;
        lock (_gate)
        {
            device = _device;
            if (device == 0 || !_current.CanSetVolume)
            {
                return false;
            }
        }

        var value = Math.Clamp(level, 0f, 1f);
        var status = CoreAudio.AudioObjectSetPropertyData(device, VolumeAddress, 0, IntPtr.Zero, sizeof(float), value);
        if (status != 0)
        {
            _log?.Invoke($"setting volume to {value:P0} failed ({status})");
            return false;
        }

        // Unmute on a deliberate level change, matching the Windows behaviour.
        if (Current is { Muted: true, CanMute: true })
        {
            SetMute(false);
        }

        Publish(Read(device, _generation));
        return true;
    }

    public bool ToggleMute()
    {
        var current = Current;
        return current is { HasDevice: true, CanMute: true } && SetMute(!current.Muted);
    }

    private bool SetMute(bool muted)
    {
        uint device;
        lock (_gate)
        {
            device = _device;
            if (device == 0 || !_current.CanMute)
            {
                return false;
            }
        }

        var value = muted ? 1u : 0u;
        var status = CoreAudio.AudioObjectSetPropertyData(device, MuteAddress, 0, IntPtr.Zero, sizeof(uint), value);
        if (status != 0)
        {
            _log?.Invoke($"setting mute to {muted} failed ({status})");
            return false;
        }

        Publish(Read(device, _generation));
        return true;
    }

    /// <summary>Point at whatever the default output device is now, moving the listeners with it.</summary>
    private void Rebind()
    {
        uint previous;
        int generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _device;
            generation = ++_generation;
        }

        if (previous != 0)
        {
            RemoveDeviceListeners(previous);
        }

        var size = (uint)sizeof(uint);
        var status = CoreAudio.AudioObjectGetPropertyData(
            CoreAudio.SystemObject, DefaultDeviceAddress, 0, IntPtr.Zero, ref size, out uint device);
        if (status != 0 || device == 0)
        {
            _log?.Invoke("no default output device");
            Publish(VolumeSnapshot.NoDevice, generation);
            return;
        }

        lock (_gate)
        {
            if (_generation != generation)
            {
                return; // another rebind overtook this one
            }

            _device = device;
        }

        CoreAudio.AudioObjectAddPropertyListener(device, VolumeAddress, _onDevicePropertyChanged, IntPtr.Zero);
        CoreAudio.AudioObjectAddPropertyListener(device, MuteAddress, _onDevicePropertyChanged, IntPtr.Zero);

        var snapshot = Read(device, generation);
        _log?.Invoke($"bound to '{snapshot.DeviceName}' (volume {(snapshot.CanSetVolume ? "settable" : "hardware only")}, mute {(snapshot.CanMute ? "available" : "unavailable")})");
        Publish(snapshot, generation);
    }

    private void RemoveDeviceListeners(uint device)
    {
        CoreAudio.AudioObjectRemovePropertyListener(device, VolumeAddress, _onDevicePropertyChanged, IntPtr.Zero);
        CoreAudio.AudioObjectRemovePropertyListener(device, MuteAddress, _onDevicePropertyChanged, IntPtr.Zero);
    }

    private void OnDeviceChanged(uint objectId)
    {
        int generation;
        lock (_gate)
        {
            if (objectId != _device)
            {
                return; // a callback from the device we just moved off
            }

            generation = _generation;
        }

        Publish(Read(objectId, generation), generation);
    }

    /// <summary>Everything the snapshot needs, in one pass, including what the device can actually do.</summary>
    private static VolumeSnapshot Read(uint device, int generation)
    {
        _ = generation;
        var name = CoreAudio.GetStringProperty(device, NameAddress);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Audio device";
        }

        var hasVolume = CoreAudio.AudioObjectHasProperty(device, VolumeAddress);
        var canSetVolume = hasVolume
            && CoreAudio.AudioObjectIsPropertySettable(device, VolumeAddress, out var settable) == 0
            && settable;

        var canMute = CoreAudio.AudioObjectHasProperty(device, MuteAddress)
            && CoreAudio.AudioObjectIsPropertySettable(device, MuteAddress, out var muteSettable) == 0
            && muteSettable;

        if (!canSetVolume && !canMute)
        {
            return VolumeSnapshot.HardwareOnly(name);
        }

        var level = 0f;
        if (hasVolume)
        {
            var size = (uint)sizeof(float);
            if (CoreAudio.AudioObjectGetPropertyData(device, VolumeAddress, 0, IntPtr.Zero, ref size, out float value) == 0)
            {
                level = Math.Clamp(value, 0f, 1f);
            }
        }

        var muted = false;
        if (canMute)
        {
            var size = (uint)sizeof(uint);
            if (CoreAudio.AudioObjectGetPropertyData(device, MuteAddress, 0, IntPtr.Zero, ref size, out uint value) == 0)
            {
                muted = value != 0;
            }
        }

        return new VolumeSnapshot(name, level, muted, true, canSetVolume, canMute);
    }

    private void Publish(VolumeSnapshot snapshot, int? generation = null)
    {
        lock (_gate)
        {
            if (_disposed || (generation is { } g && g != _generation))
            {
                return;
            }

            if (_current == snapshot)
            {
                return;
            }

            _current = snapshot;
        }

        Changed?.Invoke(snapshot);
    }

    public void Dispose()
    {
        uint device;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            device = _device;
            _device = 0;
        }

        CoreAudio.AudioObjectRemovePropertyListener(
            CoreAudio.SystemObject, DefaultDeviceAddress, _onDefaultDeviceChanged, IntPtr.Zero);
        if (device != 0)
        {
            RemoveDeviceListeners(device);
        }
    }
}
