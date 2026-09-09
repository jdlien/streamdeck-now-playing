using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NowPlaying.Device;

/// <summary>One display's brightness channel, whatever protocol reaches it.</summary>
internal interface IMacBrightnessBackend
{
    /// <summary>For the log, so a misbehaving display can be traced to the path it uses.</summary>
    string Kind { get; }

    /// <summary>
    /// Whether a read is a transaction on the display's own bus, and so can
    /// wake a sleeping monitor. True for DDC/CI, false for DisplayServices,
    /// which answers locally. Callers use it to decide whether polling is safe.
    /// </summary>
    bool ReadDisturbsDisplay { get; }

    /// <summary>Brightness 0..100, or null when the display did not answer usefully.</summary>
    int? ReadPercent();

    /// <summary>Set brightness 0..100. False when the write was refused.</summary>
    bool WritePercent(int percent);
}

/// <summary>
/// Apple displays, over the private DisplayServices framework. Apple panels do
/// not answer DDC/CI at all -- measured on both a Studio Display XDR and a 2022
/// Studio Display, whose I2C writes fail or return noise -- so this is the only
/// route to them. Brightness is a float 0..1.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class AppleBrightnessBackend(uint displayId) : IMacBrightnessBackend
{
    public string Kind => "DisplayServices";

    /// <summary>Answered locally by the window server; nothing goes near the panel.</summary>
    public bool ReadDisturbsDisplay => false;

    public int? ReadPercent()
    {
        var get = NativeMethods.DisplayServicesGetBrightness.Value;
        if (get is null || get(displayId, out var value) != 0)
        {
            return null;
        }

        if (float.IsNaN(value) || value < 0f || value > 1f)
        {
            return null;
        }

        return (int)Math.Round(value * 100);
    }

    public bool WritePercent(int percent)
    {
        var set = NativeMethods.DisplayServicesSetBrightness.Value;
        return set is not null && set(displayId, Math.Clamp(percent, 0, 100) / 100f) == 0;
    }

    /// <summary>Whether this display answers DisplayServices at all.</summary>
    public static bool Supports(uint displayId)
    {
        var can = NativeMethods.DisplayServicesCanChangeBrightness.Value;
        var get = NativeMethods.DisplayServicesGetBrightness.Value;
        if (get is null)
        {
            return false;
        }

        // CanChangeBrightness alone has been seen to answer for panels that then
        // fail to read, so require a successful read too.
        return can?.Invoke(displayId) == true && get(displayId, out var value) == 0 && value is >= 0f and <= 1f;
    }
}

/// <summary>
/// Everything else, over DDC/CI on the display's I2C bus. Every reply goes
/// through <see cref="DdcCodec"/>'s validation before it is believed, because a
/// successful transaction can still return noise (macos-port-plan D6).
///
/// The monitor's range is its own: the BenQ here reports 0..100, but the
/// Windows side has seen an Odyssey G95NC report 0..50. The range is read once
/// and reused, and percentages are converted through <see cref="BrightnessMath"/>.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class DdcBrightnessBackend(IntPtr avService, Action<string>? log = null) : IMacBrightnessBackend
{
    /// <summary>The monitor's own upper bound, learned from its first valid reply.</summary>
    private int _maximum = 100;

    /// <summary>
    /// DDC is a slow serial bus and a display will refuse a transaction that
    /// arrives too soon after the last one. Measured here: a write issued
    /// immediately after the startup read was rejected outright, while the same
    /// write with a gap succeeds.
    /// </summary>
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(120);

    private readonly object _busGate = new();
    private DateTime _lastTransaction = DateTime.MinValue;

    public string Kind => "DDC/CI";

    /// <summary>Every read is an I2C round trip, which is exactly what wakes a sleeping monitor.</summary>
    public bool ReadDisturbsDisplay => true;

    /// <summary>Serialize onto the bus and honour the inter-transaction gap.</summary>
    private void PaceBus()
    {
        var since = DateTime.UtcNow - _lastTransaction;
        if (since < MinimumGap)
        {
            Thread.Sleep(MinimumGap - since);
        }

        _lastTransaction = DateTime.UtcNow;
    }

    public int? ReadPercent()
    {
        var reading = Read();
        return reading is { } value ? BrightnessMath.ToPercent(0, (uint)value.Current, (uint)value.Maximum) : null;
    }

    public bool WritePercent(int percent)
    {
        var units = BrightnessMath.ToUnits(0, (uint)_maximum, Math.Clamp(percent, 0, 100));
        lock (_busGate)
        {
            return Write(DdcCodec.SetRequest(DdcCodec.Luminance, (int)units));
        }
    }

    private VcpReading? Read()
    {
        var read = NativeMethods.IOAVServiceReadI2C.Value;
        if (read is null)
        {
            return null;
        }

        lock (_busGate)
        {
            return ReadLocked(read);
        }
    }

    private VcpReading? ReadLocked(NativeMethods.AvServiceI2CProc read)
    {
        if (!Write(DdcCodec.GetRequest(DdcCodec.Luminance)))
        {
            return null;
        }

        // The display needs a moment before the reply is on the bus.
        Thread.Sleep(50);

        var buffer = Marshal.AllocHGlobal(12);
        try
        {
            if (read(avService, DdcCodec.ChipAddress, DdcCodec.DataAddress, buffer, 12) != 0)
            {
                return null;
            }

            var reply = new byte[12];
            Marshal.Copy(buffer, reply, 0, 12);
            return DdcCodec.ParseGetReply(reply, DdcCodec.Luminance);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Caller holds <c>_busGate</c>.</summary>
    private bool Write(byte[] frame)
    {
        var write = NativeMethods.IOAVServiceWriteI2C.Value;
        if (write is null)
        {
            return false;
        }

        PaceBus();
        var buffer = Marshal.AllocHGlobal(frame.Length);
        try
        {
            Marshal.Copy(frame, 0, buffer, frame.Length);
            var status = write(avService, DdcCodec.ChipAddress, DdcCodec.DataAddress, buffer, (uint)frame.Length);
            if (status != 0)
            {
                log?.Invoke($"I2C write failed: 0x{status:x8}");
            }

            return status == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Probe a display's DDC channel. Returns a backend only when the monitor
    /// answers with a reply that validates, so a display whose bus returns
    /// rubbish is reported as unavailable rather than controlled wrongly.
    /// </summary>
    public static DdcBrightnessBackend? Probe(IntPtr avService, Action<string>? log = null)
    {
        if (avService == IntPtr.Zero)
        {
            return null;
        }

        // The probed instance is the one returned, so its bus pacing clock
        // carries over: a write issued straight after the probe's read would
        // otherwise arrive too soon and be refused.
        var backend = new DdcBrightnessBackend(avService, log);
        if (backend.Read() is not { } reading)
        {
            return null;
        }

        backend._maximum = reading.Maximum;
        return backend;
    }
}
