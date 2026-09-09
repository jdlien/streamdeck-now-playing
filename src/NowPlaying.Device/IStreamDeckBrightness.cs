namespace NowPlaying.Device;

/// <summary>
/// The Stream Deck's own screen brightness. Applying is synchronous and quick
/// (one HID feature report per device), so callers may use it from input
/// handlers.
///
/// Windows only. macOS has no implementation and is not expected to get one:
/// the Stream Deck app opens the HID device with kIOHIDOptionsTypeSeizeDevice,
/// so a plugin cannot open it at all (macos-port-plan S2). The action is hidden
/// there rather than removed, because the block is Elgato's to lift.
/// </summary>
public interface IStreamDeckBrightness
{
    /// <summary>The brightness the plugin believes the device has. The device cannot report it.</summary>
    BrightnessState Current { get; }

    /// <summary>Raised after a change was applied (or attempted). Carries the new state and how many devices took it.</summary>
    event Action<BrightnessState, int>? Changed;

    /// <summary>Move the level by <paramref name="delta"/>. Returns how many devices took it.</summary>
    int Adjust(int delta);

    /// <summary>Flip the dim toggle. Returns how many devices took it.</summary>
    int Toggle();

    /// <summary>Replace the state outright. Returns how many devices took it.</summary>
    int Set(BrightnessState state);

    /// <summary>Send the current state again, after something may have changed the hardware behind our back.</summary>
    int Reapply();
}
