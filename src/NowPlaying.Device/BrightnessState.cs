namespace NowPlaying.Device;

/// <summary>
/// The brightness the plugin believes the device has. The device cannot
/// report brightness, so this is the source of truth: a remembered level
/// plus an "off" toggle that keeps the level for when it comes back.
/// </summary>
/// <param name="Level">The level to show and to restore, 0..100.</param>
/// <param name="Off">Whether the screen is toggled to 0 without forgetting the level.</param>
public sealed record BrightnessState(int Level, bool Off)
{
    /// <summary>What a fresh install shows before anyone touches the dial.</summary>
    public const int DefaultLevel = 60;

    /// <summary>The level to restore to when toggling on from a level of 0.</summary>
    public const int RestoreFloor = 40;

    public static BrightnessState Default { get; } = new(DefaultLevel, false);

    /// <summary>What is actually sent to the device.</summary>
    public int Effective => Off ? 0 : Level;

    /// <summary>Turn the dial: move the level and come back on if off, as adjusting volume unmutes.</summary>
    public BrightnessState Adjust(int delta) => new(Math.Clamp(Level + delta, 0, 100), false);

    /// <summary>Press: off keeps the level for later; on restores it, or a sane floor if the level was 0.</summary>
    public BrightnessState Toggle() => Off
        ? new(Level == 0 ? RestoreFloor : Level, false)
        : new(Level, true);

    public static BrightnessState FromLevel(int? saved) =>
        saved is { } level ? new(Math.Clamp(level, 0, 100), false) : Default;
}
