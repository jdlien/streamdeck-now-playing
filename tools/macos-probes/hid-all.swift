import Foundation
import IOKit
import IOKit.hid

func decode(_ r: IOReturn) -> String {
    let m: [IOReturn: String] = [kIOReturnSuccess:"success", kIOReturnExclusiveAccess:"exclusiveAccess",
        kIOReturnNotPermitted:"notPermitted", kIOReturnNotPrivileged:"notPrivileged", kIOReturnBusy:"busy",
        kIOReturnUnsupported:"unsupported", kIOReturnNoDevice:"noDevice", kIOReturnError:"error"]
    return m[r] ?? "0x\(String(format:"%08x", UInt32(bitPattern: r)))"
}
func intProp(_ s: io_service_t, _ k: String) -> Int? {
    guard let cf = IORegistryEntryCreateCFProperty(s, k as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() else { return nil }
    return (cf as? NSNumber)?.intValue
}
var iter: io_iterator_t = 0
IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOHIDDevice"), &iter)
var n = 0
while case let svc = IOIteratorNext(iter), svc != 0 {
    defer { IOObjectRelease(svc) }
    guard intProp(svc, kIOHIDVendorIDKey) == 4057 else { continue }
    n += 1
    let up = intProp(svc, kIOHIDPrimaryUsagePageKey) ?? -1
    let u  = intProp(svc, kIOHIDPrimaryUsageKey) ?? -1
    let mf = intProp(svc, kIOHIDMaxFeatureReportSizeKey) ?? 0
    let mi = intProp(svc, kIOHIDMaxInputReportSizeKey) ?? 0
    let mo = intProp(svc, kIOHIDMaxOutputReportSizeKey) ?? 0
    print("\ncollection #\(n): usagePage=\(up) usage=\(u) feature=\(mf) in=\(mi) out=\(mo)")
    guard let dev = IOHIDDeviceCreate(kCFAllocatorDefault, svc) else { print("  create -> nil"); continue }
    for (label, opts) in [("None", kIOHIDOptionsTypeNone)] {
        let r = IOHIDDeviceOpen(dev, IOOptionBits(opts))
        print("  IOHIDDeviceOpen(\(label)) -> \(decode(r))")
        if r == kIOReturnSuccess { IOHIDDeviceClose(dev, IOOptionBits(opts)) }
    }
}
IOObjectRelease(iter)
print("\ntotal Elgato HID collections: \(n)")
