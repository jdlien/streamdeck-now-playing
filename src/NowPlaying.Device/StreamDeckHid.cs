using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NowPlaying.Device;

/// <summary>
/// The one thing the plugin says to the hardware directly: brightness. The
/// app owns the device and exposes no brightness command to plugins, so
/// this sends the same HID feature report the app does. The report is the
/// V2-family one used by every current Stream Deck: 32 bytes,
/// <c>03 08 percent</c>.
/// </summary>
public static class StreamDeckHid
{
    public const ushort ElgatoVendorId = 0x0fd9;
    public const ushort StreamDeckPlusProductId = 0x0084;

    private const uint CmGetDeviceInterfaceListPresent = 0x1;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    /// <summary>
    /// HID interface paths of present devices with the given product id.
    /// Reads the PnP tree only; nothing is opened.
    /// </summary>
    public static IReadOnlyList<string> FindDevicePaths(ushort productId = StreamDeckPlusProductId)
    {
        HidD_GetHidGuid(out var hidGuid);
        var needle = $"vid_{ElgatoVendorId:x4}&pid_{productId:x4}";

        if (CM_Get_Device_Interface_List_SizeW(out var length, ref hidGuid, null, CmGetDeviceInterfaceListPresent) != 0 || length == 0)
        {
            return [];
        }

        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(ref hidGuid, null, buffer, length, CmGetDeviceInterfaceListPresent) != 0)
        {
            return [];
        }

        return new string(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>Send the brightness report to one device. Throws a Win32Exception when the open or the report fails.</summary>
    public static void SetBrightness(string devicePath, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        // Shared read/write: the Stream Deck app keeps its own handle open, and HID feature reports go through the class driver regardless.
        using var handle = CreateFileW(devicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"opening {devicePath}");
        }

        var report = new byte[32];
        report[0] = 0x03;
        report[1] = 0x08;
        report[2] = (byte)percent;
        if (!HidD_SetFeature(handle, report, report.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HidD_SetFeature");
        }
    }

    /// <summary>Apply to every present Stream Deck +. Returns how many devices took it; failures are logged, not thrown.</summary>
    public static int SetBrightnessAll(int percent, Action<string>? log = null, ushort productId = StreamDeckPlusProductId)
    {
        var applied = 0;
        foreach (var path in FindDevicePaths(productId))
        {
            try
            {
                SetBrightness(path, percent);
                applied++;
            }
            catch (Exception ex)
            {
                log?.Invoke($"brightness {percent}% failed for {path}: {ex.Message}");
            }
        }

        return applied;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClassGuid, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClassGuid, string? deviceId, char[] buffer, uint bufferLength, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
