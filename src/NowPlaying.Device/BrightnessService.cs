namespace NowPlaying.Device;

/// <summary>
/// Owns the brightness state and pushes every change to the hardware.
/// Applying is synchronous and quick (one HID feature report per device),
/// so callers may use it from input handlers.
/// </summary>
public sealed class BrightnessService
{
    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private BrightnessState _state;

    public BrightnessService(BrightnessState initial, Action<string>? log = null)
    {
        _state = initial;
        _log = log;
    }

    public BrightnessState Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Raised after a change was applied (or attempted). Carries the new state and how many devices took it.</summary>
    public event Action<BrightnessState, int>? Changed;

    public int Adjust(int delta) => Apply(s => s.Adjust(delta));

    public int Toggle() => Apply(s => s.Toggle());

    public int Set(BrightnessState state) => Apply(_ => state);

    /// <summary>Send the current state again, after the app or Windows may have changed the hardware behind our back.</summary>
    public int Reapply() => Apply(s => s);

    private int Apply(Func<BrightnessState, BrightnessState> change)
    {
        BrightnessState next;
        int applied;
        lock (_gate)
        {
            next = change(_state);
            _state = next;
            applied = StreamDeckHid.SetBrightnessAll(next.Effective, _log);
        }

        if (applied == 0)
        {
            _log?.Invoke($"brightness {next.Effective}%: no Stream Deck + accepted it");
        }

        Changed?.Invoke(next, applied);
        return applied;
    }
}
