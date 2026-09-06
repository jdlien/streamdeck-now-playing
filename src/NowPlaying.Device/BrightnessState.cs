namespace NowPlaying.Device;

/// <summary>
/// The brightness the plugin believes the device has. The device cannot
/// report brightness, so this is the source of truth: a remembered level
/// plus a "dimmed" toggle that drops the screen to a just-visible glow
/// without forgetting the level.
/// </summary>
/// <param name="Level">The level to show and to restore, 0..100.</param>
/// <param name="Dimmed">Whether the screen is toggled down to <see cref="DimLevel"/>.</param>
public sealed record BrightnessState(int Level, bool Dimmed)
{
    /// <summary>What a fresh install shows before anyone touches the dial.</summary>
    public const int DefaultLevel = 60;

    /// <summary>What the toggle dims to: still visible, so the deck never looks dead.</summary>
    public const int DimLevel = 1;

    /// <summary>The level to restore to when un-dimming from a level at or below the dim level.</summary>
    public const int RestoreFloor = 40;

    public static BrightnessState Default { get; } = new(DefaultLevel, false);

    /// <summary>What is actually sent to the device.</summary>
    public int Effective => Dimmed ? DimLevel : Level;

    /// <summary>Turn the dial: move the level and un-dim if dimmed, as adjusting volume unmutes.</summary>
    public BrightnessState Adjust(int delta) => new(Math.Clamp(Level + delta, 0, 100), false);

    /// <summary>Press: dim keeps the level for later; un-dim restores it, or a sane floor if the level was no brighter than dim.</summary>
    public BrightnessState Toggle() => Dimmed
        ? new(Level <= DimLevel ? RestoreFloor : Level, false)
        : new(Level, true);

    public static BrightnessState FromLevel(int? saved) =>
        saved is { } level ? new(Math.Clamp(level, 0, 100), false) : Default;
}
