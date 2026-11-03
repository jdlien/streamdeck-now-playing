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

    /// <summary>
    /// Move the level relative to where it is. The starting level is read
    /// together with the device it belongs to: read separately, a switch of
    /// output in between applied the old device's level, plus the step, to
    /// the new one -- a turn up from the speakers' 10% landing at 92%.
    /// </summary>
    public bool AdjustBy(float delta) => SetLevel(current => current + delta);

    public bool SetLevel(float level) => SetLevel(_ => level);

    private bool SetLevel(Func<float, float> target)
    {
        // The device and the generation it was bound under, taken together and
        // used for the whole operation. A default-output switch can land while
        // the write is in flight, and the state read back afterwards used to be
        // published unconditionally: the old device's level replaced the new
        // one's on the strip and stayed there until something else changed.
        uint device;
        int generation;
        bool muted;
        float from;
        lock (_gate)
        {
            device = _device;
            generation = _generation;
            muted = _current is { Muted: true, CanMute: true };
            from = _current.Level;
            if (device == 0 || !_current.CanSetVolume)
            {
                return false;
            }
        }

        var value = Math.Clamp(target(from), 0f, 1f);
        var status = CoreAudio.AudioObjectSetPropertyData(device, VolumeAddress, 0, IntPtr.Zero, sizeof(float), value);
        if (status != 0)
        {
            _log?.Invoke($"setting volume to {value:P0} failed ({status})");
            return false;
        }

        // Unmute on a deliberate level change, matching the Windows behaviour.
        if (muted)
        {
            SetMute(device, generation, false);
        }

        Publish(Read(device, generation), generation);
        return true;
    }

    public bool ToggleMute()
    {
        uint device;
        int generation;
        bool muted;
        lock (_gate)
        {
            device = _device;
            generation = _generation;
            muted = _current.Muted;
            if (device == 0 || !_current.HasDevice || !_current.CanMute)
            {
                return false;
            }
        }

        return SetMute(device, generation, !muted);
    }

    /// <param name="device">The device to change: the one the caller read, never whatever is bound by now.</param>
    /// <param name="generation">The binding the caller read it under; see <see cref="SetLevel(Func{float, float})"/>.</param>
    /// <param name="muted">The mute state to set.</param>
    private bool SetMute(uint device, int generation, bool muted)
    {
        var value = muted ? 1u : 0u;
        var status = CoreAudio.AudioObjectSetPropertyData(device, MuteAddress, 0, IntPtr.Zero, sizeof(uint), value);
        if (status != 0)
        {
            _log?.Invoke($"setting mute to {muted} failed ({status})");
            return false;
        }

        Publish(Read(device, generation), generation);
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

            // No device until the new one is installed. Otherwise a write in
            // between paired the old device with the new generation, and its
            // read-back passed the generation check and was published over
            // the new device's state.
            _device = 0;
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

        CoreAudio.AudioObjectAddPropertyListener(device, VolumeAddress, _onDevicePropertyChanged, IntPtr.Zero);
        CoreAudio.AudioObjectAddPropertyListener(device, MuteAddress, _onDevicePropertyChanged, IntPtr.Zero);

        // Read first, then install the device and its state together. With the
        // device installed before its state was read, a turn in between saw the
        // new device next to the previous output's level -- from 90% on the
        // old output, a step up wrote 92% to speakers that had been at 10%.
        var snapshot = Read(device, generation);
        bool changed;
        lock (_gate)
        {
            if (_disposed || _generation != generation)
            {
                return; // another rebind overtook this one
            }

            _device = device;
            changed = _current != snapshot;
            _current = snapshot;
        }

        _log?.Invoke($"bound to '{snapshot.DeviceName}' (volume {(snapshot.CanSetVolume ? "settable" : "hardware only")}, mute {(snapshot.CanMute ? "available" : "unavailable")})");
        if (changed)
        {
            Changed?.Invoke(snapshot);
        }
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
