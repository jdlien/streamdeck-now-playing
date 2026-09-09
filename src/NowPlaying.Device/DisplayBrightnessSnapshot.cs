namespace NowPlaying.Device;

/// <summary>One monitor's brightness as the plugin knows it.</summary>
/// <param name="Name">The monitor's name from its EDID, or the driver's description.</param>
/// <param name="Level">Brightness as a percent of the monitor's own range; the level to restore when dimmed.</param>
/// <param name="Dimmed">Whether the toggle has taken the monitor to its minimum without forgetting the level.</param>
/// <param name="Available">False when this monitor does not answer DDC/CI or is not connected.</param>
/// <param name="Index">Position among the monitors that answer, 0-based, primary first.</param>
/// <param name="Count">How many monitors answer DDC/CI.</param>
public sealed record DisplayBrightnessSnapshot(string Name, int Level, bool Dimmed, bool Available, int Index = 0, int Count = 0)
{
    public static DisplayBrightnessSnapshot Unavailable { get; } = new("No DDC/CI monitor", 0, false, false);

    /// <summary>What is sent to the monitor, as a percent.</summary>
    public int Effective => Dimmed ? 0 : Level;
}
