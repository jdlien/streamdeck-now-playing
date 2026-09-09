using System.Runtime.InteropServices;

namespace NowPlaying.Audio;

/// <summary>
/// The slice of CoreAudio the volume service needs. All public API, so no
/// entitlement and no permission prompt, unlike the private frameworks the
/// display backends need.
///
/// The listener is the non-block <c>AudioObjectAddPropertyListener</c> rather
/// than <c>AudioObjectAddPropertyListenerBlock</c>: the block variant takes a
/// real Objective-C block, which a plain delegate cannot be marshalled as
/// (macos-port-plan D3).
/// </summary>
internal static partial class CoreAudio
{
    private const string Library = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint SystemObject = 1;

    /// <summary>Four-character codes, as CoreAudio spells them.</summary>
    public static uint FourCC(string code) =>
        ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];

    public static readonly uint DefaultOutputDevice = FourCC("dOut");
    public static readonly uint ScopeGlobal = FourCC("glob");
    public static readonly uint ScopeOutput = FourCC("outp");
    public static readonly uint ObjectName = FourCC("lnam");

    /// <summary>
    /// The volume the menu-bar slider drives. Not every device has it: an
    /// interface whose gain is a physical knob exposes none at all.
    /// </summary>
    public static readonly uint VirtualMainVolume = FourCC("vmvc");

    public static readonly uint Mute = FourCC("mute");

    public const uint ElementMain = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyAddress(uint selector, uint scope, uint element)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    /// <summary>Signature of a property listener. Called on a CoreAudio thread.</summary>
    public delegate int ListenerProc(uint objectId, uint addressCount, IntPtr addresses, IntPtr clientData);

    [LibraryImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool AudioObjectHasProperty(uint objectId, in PropertyAddress address);

    [LibraryImport(Library)]
    public static partial int AudioObjectIsPropertySettable(uint objectId, in PropertyAddress address, [MarshalAs(UnmanagedType.U1)] out bool settable);

    [LibraryImport(Library)]
    public static partial int AudioObjectGetPropertyDataSize(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, out uint dataSize);

    [LibraryImport(Library)]
    public static partial int AudioObjectGetPropertyData(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, out uint data);

    [LibraryImport(Library)]
    public static partial int AudioObjectGetPropertyData(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, out float data);

    [LibraryImport(Library)]
    public static partial int AudioObjectGetPropertyData(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, ref uint dataSize, out IntPtr data);

    [LibraryImport(Library)]
    public static partial int AudioObjectSetPropertyData(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, uint dataSize, in float data);

    [LibraryImport(Library)]
    public static partial int AudioObjectSetPropertyData(uint objectId, in PropertyAddress address, uint qualifierSize, IntPtr qualifier, uint dataSize, in uint data);

    [LibraryImport(Library)]
    public static partial int AudioObjectAddPropertyListener(uint objectId, in PropertyAddress address, ListenerProc listener, IntPtr clientData);

    [LibraryImport(Library)]
    public static partial int AudioObjectRemovePropertyListener(uint objectId, in PropertyAddress address, ListenerProc listener, IntPtr clientData);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFStringGetLength(IntPtr str);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(IntPtr str, Span<byte> buffer, long bufferSize, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);

    private const uint Utf8Encoding = 0x08000100;

    /// <summary>Read a CFString property and release it. Empty string when absent.</summary>
    public static string GetStringProperty(uint objectId, in PropertyAddress address)
    {
        if (!AudioObjectHasProperty(objectId, address))
        {
            return "";
        }

        var size = (uint)IntPtr.Size;
        if (AudioObjectGetPropertyData(objectId, address, 0, IntPtr.Zero, ref size, out IntPtr cfString) != 0 || cfString == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            // Worst case for UTF-8 is 3 bytes per UTF-16 unit, plus the terminator.
            var capacity = ((int)CFStringGetLength(cfString) * 3) + 1;
            Span<byte> buffer = capacity <= 256 ? stackalloc byte[256] : new byte[capacity];
            return CFStringGetCString(cfString, buffer, buffer.Length, Utf8Encoding)
                ? System.Text.Encoding.UTF8.GetString(buffer[..buffer.IndexOf((byte)0)])
                : "";
        }
        finally
        {
            CFRelease(cfString);
        }
    }
}
