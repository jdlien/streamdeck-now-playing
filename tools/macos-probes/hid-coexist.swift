// S2 spike: can we send a brightness feature report to the Stream Deck while
// the Elgato Stream Deck app owns the device?
//
// The Windows path opens with FILE_SHARE_READ|FILE_SHARE_WRITE and coexists.
// The macOS question is whether the app opens with kIOHIDOptionsTypeSeizeDevice,
// which would refuse a second opener.
//
//   swiftc -o hid-coexist hid-coexist.swift && ./hid-coexist [percent]
//
// With no argument it only enumerates and opens; it does not write.

import Foundation
import IOKit
import IOKit.hid

let elgatoVendorId = 4057            // 0x0fd9
let streamDeckPlusProductId = 132    // 0x0084

func decode(_ r: IOReturn) -> String {
    let map: [IOReturn: String] = [
        kIOReturnSuccess: "success", kIOReturnNotPermitted: "notPermitted",
        kIOReturnNotPrivileged: "notPrivileged", kIOReturnExclusiveAccess: "exclusiveAccess",
        kIOReturnBusy: "busy", kIOReturnUnsupported: "unsupported",
        kIOReturnNoDevice: "noDevice", kIOReturnBadArgument: "badArgument",
        kIOReturnError: "error", kIOReturnNotOpen: "notOpen",
    ]
    return map[r] ?? "0x\(String(format: "%08x", UInt32(bitPattern: r)))"
}

func intProp(_ svc: io_service_t, _ key: String) -> Int? {
    guard let cf = IORegistryEntryCreateCFProperty(svc, key as CFString, kCFAllocatorDefault, 0)?
        .takeRetainedValue() else { return nil }
    return (cf as? NSNumber)?.intValue
}

let percent: Int? = CommandLine.arguments.count > 1 ? Int(CommandLine.arguments[1]) : nil

var iter: io_iterator_t = 0
guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOHIDDevice"), &iter) == KERN_SUCCESS else {
    print("IOServiceGetMatchingServices failed"); exit(1)
}

var candidates: [(io_service_t, Int)] = []   // service, MaxFeatureReportSize
while case let svc = IOIteratorNext(iter), svc != 0 {
    guard intProp(svc, kIOHIDVendorIDKey) == elgatoVendorId,
          intProp(svc, kIOHIDProductIDKey) == streamDeckPlusProductId else {
        IOObjectRelease(svc); continue
    }
    let maxFeature = intProp(svc, kIOHIDMaxFeatureReportSizeKey) ?? 0
    let usagePage = intProp(svc, kIOHIDPrimaryUsagePageKey) ?? -1
    let usage = intProp(svc, kIOHIDPrimaryUsageKey) ?? -1
    print("candidate: usagePage=\(usagePage) usage=\(usage) maxFeatureReportSize=\(maxFeature)")
    // The brightness feature report is 32 bytes; only the collection that
    // supports feature reports of that size is usable.
    if maxFeature >= 32 { candidates.append((svc, maxFeature)) } else { IOObjectRelease(svc) }
}
IOObjectRelease(iter)

guard let (service, _) = candidates.first else { print("no Stream Deck Plus HID collection with feature reports found"); exit(2) }
print("\nusing the collection with maxFeatureReportSize >= 32")

guard let device = IOHIDDeviceCreate(kCFAllocatorDefault, service) else {
    print("IOHIDDeviceCreate -> nil"); exit(3)
}

// The whole question: a non-seizing open while the Elgato app holds the device.
let openResult = IOHIDDeviceOpen(device, IOOptionBits(kIOHIDOptionsTypeNone))
print("IOHIDDeviceOpen(kIOHIDOptionsTypeNone) -> \(decode(openResult))")
if openResult != kIOReturnSuccess {
    print("\n==> OPEN REFUSED: another client holds the device with kIOHIDOptionsTypeSeizeDevice.")
    exit(4)
}
print("==> OPEN SUCCEEDED (check separately whether the Stream Deck app is running:\n    it seizes the device a few seconds after launch, so an open right after\n    relaunching the app can succeed transiently)")

guard let percent else {
    print("\n(no percent argument given; not writing. Pass a percent to test an actual write.)")
    IOHIDDeviceClose(device, IOOptionBits(kIOHIDOptionsTypeNone))
    exit(0)
}

// Same report the Windows build sends: 32 bytes, 03 08 <percent>.
var report = [UInt8](repeating: 0, count: 32)
report[0] = 0x03; report[1] = 0x08; report[2] = UInt8(max(0, min(100, percent)))
let setResult = IOHIDDeviceSetReport(device, kIOHIDReportTypeFeature, CFIndex(report[0]), report, report.count)
print("IOHIDDeviceSetReport(feature, id=3, \(percent)%) -> \(decode(setResult))")
IOHIDDeviceClose(device, IOOptionBits(kIOHIDOptionsTypeNone))
print(setResult == kIOReturnSuccess
      ? "==> WRITE ACCEPTED (confirm visually: the deck cannot report its brightness)"
      : "==> WRITE REJECTED")
