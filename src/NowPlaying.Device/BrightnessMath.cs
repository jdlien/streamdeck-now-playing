namespace NowPlaying.Device;

/// <summary>
/// Conversion between a monitor's own brightness range and the percent the
/// dial shows. Pure, and shared by every backend: DDC/CI reports an arbitrary
/// min/max per monitor (the G95NC answers 0..50), and the macOS backends have
/// the same problem with different numbers.
/// </summary>
public static class BrightnessMath
{
    /// <summary>Percent 0..100 from a value in the monitor's range.</summary>
    public static int ToPercent(uint min, uint current, uint max)
    {
        if (max <= min)
        {
            return 0;
        }

        var clamped = Math.Clamp(current, min, max);
        return (int)Math.Round((clamped - min) * 100.0 / (max - min));
    }

    /// <summary>A value in the monitor's range from a percent 0..100.</summary>
    public static uint ToUnits(uint min, uint max, int percent)
    {
        if (max <= min)
        {
            return min;
        }

        var p = Math.Clamp(percent, 0, 100);
        return min + (uint)Math.Round(p / 100.0 * (max - min));
    }
}
