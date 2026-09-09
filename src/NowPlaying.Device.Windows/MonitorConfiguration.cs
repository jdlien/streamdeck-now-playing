using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NowPlaying.Device;

/// <summary>A DDC/CI-capable physical monitor behind a Windows display.</summary>
/// <param name="Handle">The physical monitor handle; destroy it with <see cref="MonitorConfiguration.Destroy"/>.</param>
/// <param name="GdiDeviceName">The display's GDI name, e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="Name">The monitor's own name from its EDID when available, else the driver's description.</param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
public sealed record PhysicalMonitor(IntPtr Handle, string GdiDeviceName, string Name, bool IsPrimary);

/// <summary>
/// Windows' Monitor Configuration API (dxva2.dll), which speaks DDC/CI to the
/// monitor over the display cable, plus the display configuration API for
/// the monitor's real name. Every dxva2 call is a round trip to the monitor:
/// tens of milliseconds normally, over a second for the capabilities string,
/// and seconds when the monitor is asleep. Callers keep them off input paths.
/// </summary>
public static class MonitorConfiguration
{
    private const uint MonitorInfoPrimary = 0x1;
    private const uint QdcOnlyActivePaths = 0x2;
    private const uint DeviceInfoGetSourceName = 1;
    private const uint DeviceInfoGetTargetName = 2;

    /// <summary>Every physical monitor Windows can address, primary first. Reads names; does not touch brightness.</summary>
    public static IReadOnlyList<PhysicalMonitor> Enumerate(Action<string>? log = null)
    {
        var results = new List<PhysicalMonitor>();
        var friendlyNames = FriendlyNamesByGdiDevice(log);

        var monitors = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            monitors.Add(handle);
            return true;
        }, IntPtr.Zero);

        foreach (var monitor in monitors)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfoW(monitor, ref info))
            {
                continue;
            }

            var gdiName = info.szDevice ?? "";
            var primary = (info.dwFlags & MonitorInfoPrimary) != 0;

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
            {
                log?.Invoke($"{gdiName}: no physical monitors");
                continue;
            }

            var physical = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
            {
                log?.Invoke($"{gdiName}: GetPhysicalMonitorsFromHMONITOR failed ({Marshal.GetLastWin32Error()})");
                continue;
            }

            foreach (var p in physical)
            {
                friendlyNames.TryGetValue(gdiName, out var friendly);
                var name = !string.IsNullOrWhiteSpace(friendly) ? friendly : (p.szPhysicalMonitorDescription ?? "Monitor").Trim();
                results.Add(new PhysicalMonitor(p.hPhysicalMonitor, gdiName, name, primary));
            }
        }

        return results.OrderByDescending(m => m.IsPrimary).ToArray();
    }

    /// <summary>Release handles from <see cref="Enumerate"/> that are not being kept.</summary>
    public static void Destroy(IEnumerable<PhysicalMonitor> monitors)
    {
        foreach (var monitor in monitors)
        {
            try
            {
                DestroyPhysicalMonitor(monitor.Handle);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// DDC/CI transactions fail transiently, especially right after another
    /// one on the same bus (seen on the Odyssey G95NC: a read that had just
    /// succeeded failed when repeated within milliseconds). Every call gets a
    /// few attempts with a pause between them before it counts as a failure.
    /// </summary>
    public const int Attempts = 5;

    /// <summary>Backoff between attempts: 100, 200, 300, 400 ms, about a second in all.</summary>
    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(100 * attempt);

    /// <summary>Current brightness and the monitor's own range. Throws when the monitor does not answer after <see cref="Attempts"/> tries.</summary>
    public static (uint Min, uint Current, uint Max) ReadBrightness(IntPtr handle)
    {
        var error = 0;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (GetMonitorBrightness(handle, out var min, out var current, out var max))
            {
                return (min, current, max);
            }

            error = Marshal.GetLastWin32Error();
            if (attempt < Attempts)
            {
                Thread.Sleep(RetryDelay(attempt));
            }
        }

        throw new Win32Exception(error, $"GetMonitorBrightness failed {Attempts} times: 0x{error:x8} {new Win32Exception(error).Message}");
    }

    /// <summary>Set brightness in the monitor's own units. Throws when the monitor does not answer after <see cref="Attempts"/> tries.</summary>
    public static void WriteBrightness(IntPtr handle, uint value)
    {
        var error = 0;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (SetMonitorBrightness(handle, value))
            {
                return;
            }

            error = Marshal.GetLastWin32Error();
            if (attempt < Attempts)
            {
                Thread.Sleep(RetryDelay(attempt));
            }
        }

        throw new Win32Exception(error, $"SetMonitorBrightness failed {Attempts} times: 0x{error:x8} {new Win32Exception(error).Message}");
    }

    // -- friendly names via the display configuration API -------------------

    /// <summary>GDI device name (\\.\DISPLAYn) to the monitor's EDID friendly name, for every active path.</summary>
    private static Dictionary<string, string> FriendlyNamesByGdiDevice(Action<string>? log)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
            {
                return map;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return map;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DeviceInfoGetSourceName,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0)
                {
                    continue;
                }

                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DeviceInfoGetTargetName,
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = paths[i].targetInfo.adapterId,
                        id = paths[i].targetInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref target) != 0)
                {
                    continue;
                }

                var friendly = (target.monitorFriendlyDeviceName ?? "").Trim();
                if (friendly.Length > 0 && !string.IsNullOrEmpty(source.viewGdiDeviceName))
                {
                    map[source.viewGdiDeviceName] = friendly;
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"display names unavailable: {ex.Message}");
        }

        return map;
    }

    // -- interop ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    /// <summary>Only the size matters; the modes are not read.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(IntPtr hPhysicalMonitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hPhysicalMonitor, out uint min, out uint current, out uint max);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hPhysicalMonitor, uint brightness);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME packet);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME packet);
}
