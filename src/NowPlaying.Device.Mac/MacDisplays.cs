using System.Runtime.Versioning;

namespace NowPlaying.Device;

/// <summary>A display macOS can address, paired with the IOKit service that speaks DDC to it.</summary>
/// <param name="DisplayId">The ephemeral <c>CGDirectDisplayID</c>. Changes across reboots and re-cabling.</param>
/// <param name="Name">The panel's own name from its EDID, e.g. "Studio Display XDR" or "BenQ MA270S".</param>
/// <param name="IsMain">Whether this is the main display.</param>
/// <param name="VendorId">EDID manufacturer id.</param>
/// <param name="ProductId">EDID product id.</param>
/// <param name="SerialNumber">EDID serial. Not unique on every panel, so never used alone.</param>
/// <param name="AvService">The IOAVService for DDC, or zero when this display has none.</param>
internal sealed record MacDisplay(
    uint DisplayId,
    string Name,
    bool IsMain,
    uint VendorId,
    uint ProductId,
    uint SerialNumber,
    IntPtr AvService);

/// <summary>
/// Enumerates displays and pairs each one with its DDC channel.
///
/// The pairing is the part with no documented answer. CoreGraphics knows a
/// display's EDID vendor/product/serial but not its IOKit service; the
/// <c>DCPAVServiceProxy</c> nodes that carry the I2C bus know only a
/// framebuffer index (<c>dispext1</c>, <c>dispext2</c>, ...) and nothing about
/// the panel. The join is the <c>IOMobileFramebufferShim</c> node in between,
/// which has both: <c>DisplayAttributes.ProductAttributes</c> holds the EDID
/// product id and serial, and its ancestry names the framebuffer.
///
/// Measured on the target machine, matching product id *and* serial identified
/// all three displays unambiguously, including two Apple panels of different
/// generations (macos-port-plan D8).
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacDisplays
{
    public static IReadOnlyList<MacDisplay> Enumerate(Action<string>? log = null)
    {
        var framebuffers = FramebuffersByPanel(log);
        var avServices = AvServicesByFramebuffer(log);

        NativeMethods.CGGetOnlineDisplayList(0, null, out var count);
        if (count == 0)
        {
            return [];
        }

        var ids = new uint[count];
        NativeMethods.CGGetOnlineDisplayList(count, ids, out count);

        var displays = new List<MacDisplay>();
        foreach (var id in ids.Take((int)count))
        {
            var vendor = NativeMethods.CGDisplayVendorNumber(id);
            var product = NativeMethods.CGDisplayModelNumber(id);
            var serial = NativeMethods.CGDisplaySerialNumber(id);

            var key = (product, serial);
            framebuffers.TryGetValue(key, out var panel);

            var name = !string.IsNullOrWhiteSpace(panel.Name) ? panel.Name : $"Display {id}";
            var av = IntPtr.Zero;
            if (!string.IsNullOrEmpty(panel.Framebuffer) && avServices.TryGetValue(panel.Framebuffer, out var service))
            {
                av = service;
            }

            displays.Add(new MacDisplay(id, name, NativeMethods.CGDisplayIsMain(id), vendor, product, serial, av));
        }

        // Main display first, matching the Windows service's primary-first order.
        return [.. displays.OrderByDescending(d => d.IsMain).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>EDID (product, serial) to the panel's name and framebuffer, from the IOMobileFramebufferShim nodes.</summary>
    private static Dictionary<(uint Product, uint Serial), (string Name, string Framebuffer)> FramebuffersByPanel(Action<string>? log)
    {
        var result = new Dictionary<(uint, uint), (string, string)>();
        if (NativeMethods.IORegistryCreateIterator(0, "IOService", NativeMethods.IORegistryIterateRecursively, out var iterator) != NativeMethods.KernSuccess)
        {
            log?.Invoke("could not walk the IO registry for displays");
            return result;
        }

        try
        {
            uint entry;
            while ((entry = NativeMethods.IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    var attributes = NativeMethods.CopyProperty(entry, "DisplayAttributes");
                    if (attributes == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        var productAttributes = NativeMethods.CopyPropertyValue(attributes, "ProductAttributes");
                        if (productAttributes == IntPtr.Zero)
                        {
                            continue;
                        }

                        var product = NativeMethods.DictionaryNumber(productAttributes, "ProductID");
                        var serial = NativeMethods.DictionaryNumber(productAttributes, "SerialNumber");
                        if (product is null || serial is null)
                        {
                            continue;
                        }

                        // EDID names are whatever the manufacturer typed; the 2022
                        // Studio Display reports "StudioDisplay" with no space.
                        var name = DisplayNameNormalizer.Normalize(NativeMethods.DictionaryString(productAttributes, "ProductName"));
                        result[((uint)product.Value, (uint)serial.Value)] = (name, FramebufferOf(entry));
                    }
                    finally
                    {
                        NativeMethods.CFRelease(attributes);
                    }
                }
                finally
                {
                    NativeMethods.IOObjectRelease(entry);
                }
            }
        }
        finally
        {
            NativeMethods.IOObjectRelease(iterator);
        }

        return result;
    }

    /// <summary>The <c>dispextN</c> ancestor of a framebuffer node, or empty when it has none.</summary>
    private static string FramebufferOf(uint entry)
    {
        var node = entry;
        NativeMethods.IOObjectRetain(node);
        try
        {
            for (var depth = 0; depth < 12; depth++)
            {
                var name = NativeMethods.EntryName(node);
                if (name.StartsWith("dispext", StringComparison.OrdinalIgnoreCase))
                {
                    return name.Split(':')[0];
                }

                if (NativeMethods.IORegistryEntryGetParentEntry(node, "IOService", out var parent) != NativeMethods.KernSuccess)
                {
                    return "";
                }

                NativeMethods.IOObjectRelease(node);
                node = parent;
            }

            return "";
        }
        finally
        {
            NativeMethods.IOObjectRelease(node);
        }
    }

    /// <summary>Framebuffer name (<c>dispext2</c>) to a live IOAVService for its I2C bus.</summary>
    private static Dictionary<string, IntPtr> AvServicesByFramebuffer(Action<string>? log)
    {
        var result = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        var create = NativeMethods.IOAVServiceCreateWithService.Value;
        if (create is null)
        {
            log?.Invoke("IOAVServiceCreateWithService is unavailable; no DDC on this system");
            return result;
        }

        var matching = NativeMethods.IOServiceMatching("DCPAVServiceProxy");
        if (NativeMethods.IOServiceGetMatchingServices(0, matching, out var iterator) != NativeMethods.KernSuccess)
        {
            return result;
        }

        try
        {
            uint entry;
            while ((entry = NativeMethods.IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    // The built-in panel is "Embedded" and has no DDC.
                    var location = NativeMethods.CopyProperty(entry, "Location");
                    var isExternal = false;
                    if (location != IntPtr.Zero)
                    {
                        isExternal = NativeMethods.CFStringValue(location) == "External";
                        NativeMethods.CFRelease(location);
                    }

                    if (!isExternal)
                    {
                        continue;
                    }

                    if (NativeMethods.IORegistryEntryGetParentEntry(entry, "IOService", out var parent) != NativeMethods.KernSuccess)
                    {
                        continue;
                    }

                    var framebuffer = NativeMethods.EntryName(parent).Split(':')[0];
                    NativeMethods.IOObjectRelease(parent);
                    if (framebuffer.Length == 0 || result.ContainsKey(framebuffer))
                    {
                        continue;
                    }

                    var service = create(IntPtr.Zero, entry);
                    if (service != IntPtr.Zero)
                    {
                        result[framebuffer] = service;
                    }
                }
                finally
                {
                    NativeMethods.IOObjectRelease(entry);
                }
            }
        }
        finally
        {
            NativeMethods.IOObjectRelease(iterator);
        }

        return result;
    }

    /// <summary>
    /// Release AV services that nothing adopted. Anything handed to a live
    /// backend must be released by that backend instead: the I2C calls write
    /// through this pointer, so releasing it early is a use-after-free that
    /// shows up as a plausible-looking I2C error rather than a crash.
    /// </summary>
    public static void Release(IEnumerable<MacDisplay> displays)
    {
        foreach (var display in displays.Where(d => d.AvService != IntPtr.Zero))
        {
            NativeMethods.CFRelease(display.AvService);
        }
    }
}
