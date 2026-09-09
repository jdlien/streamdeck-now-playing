using System.Runtime.InteropServices;

namespace NowPlaying.Device;

/// <summary>
/// The macOS surface the display backends need. Three sources, deliberately
/// separated because they carry different risk:
///
/// CoreGraphics is public API. DisplayServices and the IOAVService I2C calls
/// are private, reached by <c>dlopen</c>, and can change in any macOS release;
/// every entry point is resolved lazily and treated as absent rather than
/// fatal when it is not there (macos-port-plan D5, risk 3).
/// </summary>
internal static partial class NativeMethods
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string DisplayServicesPath = "/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices";

    // -- CoreGraphics (public) ----------------------------------------------

    [LibraryImport(CoreGraphics)]
    public static partial int CGGetOnlineDisplayList(uint maxDisplays, [Out] uint[]? displays, out uint count);

    [LibraryImport(CoreGraphics)]
    public static partial uint CGDisplayVendorNumber(uint display);

    [LibraryImport(CoreGraphics)]
    public static partial uint CGDisplayModelNumber(uint display);

    [LibraryImport(CoreGraphics)]
    public static partial uint CGDisplaySerialNumber(uint display);

    [LibraryImport(CoreGraphics)]
    public static partial uint CGDisplayUnitNumber(uint display);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CGDisplayIsMain(uint display);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CGDisplayIsBuiltin(uint display);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CGDisplayIsAsleep(uint display);

    /// <summary>Fires when displays are added, removed, or rearranged.</summary>
    public delegate void DisplayReconfigurationCallback(uint display, uint flags, IntPtr userInfo);

    [LibraryImport(CoreGraphics)]
    public static partial int CGDisplayRegisterReconfigurationCallback(DisplayReconfigurationCallback callback, IntPtr userInfo);

    [LibraryImport(CoreGraphics)]
    public static partial int CGDisplayRemoveReconfigurationCallback(DisplayReconfigurationCallback callback, IntPtr userInfo);

    // -- IOKit (public registry access; the I2C calls below are not) --------

    public const uint KernSuccess = 0;

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr IOServiceMatching(string name);

    [LibraryImport(IOKit)]
    public static partial int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);

    [LibraryImport(IOKit)]
    public static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKit)]
    public static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKit)]
    public static partial int IOObjectRetain(uint obj);

    [LibraryImport(IOKit)]
    public static partial int IORegistryEntryGetName(uint entry, [Out] byte[] name);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int IORegistryEntryGetParentEntry(uint entry, string plane, out uint parent);

    [LibraryImport(IOKit)]
    public static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(IOKit, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int IORegistryCreateIterator(uint mainPort, string plane, uint options, out uint iterator);

    public const uint IORegistryIterateRecursively = 1;

    // -- CoreFoundation, enough to read the registry dictionaries -----------

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string cstr, uint encoding);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    public static partial IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CFNumberGetValue(IntPtr number, nint type, out long value);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool CFStringGetCString(IntPtr str, [Out] byte[] buffer, long bufferSize, uint encoding);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFGetTypeID(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial nint CFNumberGetTypeID();

    public const uint Utf8 = 0x08000100;
    public const nint CFNumberSInt64Type = 4;

    // -- DisplayServices (private, dlopen) ----------------------------------

    public delegate int GetBrightnessProc(uint display, out float brightness);

    public delegate int SetBrightnessProc(uint display, float brightness);

    [return: MarshalAs(UnmanagedType.U1)]
    public delegate bool CanChangeBrightnessProc(uint display);

    private static readonly Lazy<IntPtr> DisplayServicesHandle = new(() =>
        NativeLibrary.TryLoad(DisplayServicesPath, out var handle) ? handle : IntPtr.Zero);

    public static readonly Lazy<GetBrightnessProc?> DisplayServicesGetBrightness =
        new(() => Resolve<GetBrightnessProc>(DisplayServicesHandle.Value, "DisplayServicesGetBrightness"));

    public static readonly Lazy<SetBrightnessProc?> DisplayServicesSetBrightness =
        new(() => Resolve<SetBrightnessProc>(DisplayServicesHandle.Value, "DisplayServicesSetBrightness"));

    public static readonly Lazy<CanChangeBrightnessProc?> DisplayServicesCanChangeBrightness =
        new(() => Resolve<CanChangeBrightnessProc>(DisplayServicesHandle.Value, "DisplayServicesCanChangeBrightness"));

    /// <summary>
    /// Fired when a display's brightness changes, whoever changed it. The
    /// context argument comes back as zero in practice, so the display id is
    /// the only identification to rely on.
    /// </summary>
    public delegate void BrightnessChangeCallback(uint context, uint display, IntPtr notification, IntPtr userInfo);

    public delegate int RegisterBrightnessProc(uint display, uint context, BrightnessChangeCallback callback);

    public delegate int UnregisterBrightnessProc(uint display, uint context);

    public static readonly Lazy<RegisterBrightnessProc?> DisplayServicesRegisterForBrightnessChangeNotifications =
        new(() => Resolve<RegisterBrightnessProc>(DisplayServicesHandle.Value, "DisplayServicesRegisterForBrightnessChangeNotifications"));

    public static readonly Lazy<UnregisterBrightnessProc?> DisplayServicesUnregisterForBrightnessChangeNotifications =
        new(() => Resolve<UnregisterBrightnessProc>(DisplayServicesHandle.Value, "DisplayServicesUnregisterForBrightnessChangeNotifications"));

    // -- IOAVService (private, dlopen out of IOKit) -------------------------

    public delegate IntPtr AvServiceCreateProc(IntPtr allocator, uint service);

    public delegate int AvServiceI2CProc(IntPtr service, uint chipAddress, uint offset, IntPtr buffer, uint size);

    private static readonly Lazy<IntPtr> IOKitHandle = new(() =>
        NativeLibrary.TryLoad(IOKit, out var handle) ? handle : IntPtr.Zero);

    public static readonly Lazy<AvServiceCreateProc?> IOAVServiceCreateWithService =
        new(() => Resolve<AvServiceCreateProc>(IOKitHandle.Value, "IOAVServiceCreateWithService"));

    public static readonly Lazy<AvServiceI2CProc?> IOAVServiceReadI2C =
        new(() => Resolve<AvServiceI2CProc>(IOKitHandle.Value, "IOAVServiceReadI2C"));

    public static readonly Lazy<AvServiceI2CProc?> IOAVServiceWriteI2C =
        new(() => Resolve<AvServiceI2CProc>(IOKitHandle.Value, "IOAVServiceWriteI2C"));

    private static T? Resolve<T>(IntPtr library, string symbol) where T : Delegate =>
        library != IntPtr.Zero && NativeLibrary.TryGetExport(library, symbol, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    // -- small helpers over the CF calls above ------------------------------

    /// <summary>Read a registry property as a CF object. The caller releases it.</summary>
    public static IntPtr CopyProperty(uint entry, string key)
    {
        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);
        try
        {
            return IORegistryEntryCreateCFProperty(entry, cfKey, IntPtr.Zero, 0);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>A value out of a CFDictionary, as a string. Empty when absent or not a string.</summary>
    public static string DictionaryString(IntPtr dict, string key)
    {
        var value = DictionaryValue(dict, key);
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
        {
            return "";
        }

        var buffer = new byte[512];
        if (!CFStringGetCString(value, buffer, buffer.Length, Utf8))
        {
            return "";
        }

        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    /// <summary>A value out of a CFDictionary, as a number. Null when absent or not a number.</summary>
    public static long? DictionaryNumber(IntPtr dict, string key)
    {
        var value = DictionaryValue(dict, key);
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFNumberGetTypeID())
        {
            return null;
        }

        return CFNumberGetValue(value, CFNumberSInt64Type, out var result) ? result : null;
    }

    /// <summary>A nested CFDictionary (or any CF value) out of a dictionary. Borrowed, not owned.</summary>
    public static IntPtr CopyPropertyValue(IntPtr dict, string key) => DictionaryValue(dict, key);

    /// <summary>A CFString as a managed string. Empty when it is not a string.</summary>
    public static string CFStringValue(IntPtr value)
    {
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
        {
            return "";
        }

        var buffer = new byte[256];
        if (!CFStringGetCString(value, buffer, buffer.Length, Utf8))
        {
            return "";
        }

        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static IntPtr DictionaryValue(IntPtr dict, string key)
    {
        if (dict == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var cfKey = CFStringCreateWithCString(IntPtr.Zero, key, Utf8);
        try
        {
            return CFDictionaryGetValue(dict, cfKey);
        }
        finally
        {
            CFRelease(cfKey);
        }
    }

    /// <summary>The registry node's name.</summary>
    public static string EntryName(uint entry)
    {
        var buffer = new byte[256];
        if (IORegistryEntryGetName(entry, buffer) != KernSuccess)
        {
            return "";
        }

        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}
