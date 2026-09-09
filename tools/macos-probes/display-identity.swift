// S3 follow-up: what can pair a CGDirectDisplayID with its DCPAVServiceProxy?
// Prints both sides so the join can be designed from data, not guesswork.
import Foundation
import CoreGraphics
import IOKit

print("=== CoreGraphics side ===")
var count: UInt32 = 0
CGGetOnlineDisplayList(0, nil, &count)
var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
CGGetOnlineDisplayList(count, &ids, &count)
for id in ids {
    print("display \(id): vendor=0x\(String(CGDisplayVendorNumber(id), radix:16)) model=0x\(String(CGDisplayModelNumber(id), radix:16)) serial=0x\(String(CGDisplaySerialNumber(id), radix:16)) builtin=\(CGDisplayIsBuiltin(id) != 0) main=\(CGDisplayIsMain(id) != 0) unit=\(CGDisplayUnitNumber(id))")
}

print("\n=== IOKit side: DCPAVServiceProxy and ancestors ===")
var iter: io_iterator_t = 0
IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("DCPAVServiceProxy"), &iter)
var n = 0
while case let svc = IOIteratorNext(iter), svc != 0 {
    defer { IOObjectRelease(svc) }
    n += 1
    let loc = IORegistryEntryCreateCFProperty(svc, "Location" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? String ?? "?"
    print("\n#\(n) Location=\(loc)")
    // Walk up looking for anything that identifies the panel.
    var node = svc
    IOObjectRetain(node)
    for depth in 0..<8 {
        var nb = [CChar](repeating: 0, count: 200)
        IORegistryEntryGetName(node, &nb)
        let name = String(cString: nb)
        var interesting: [String] = []
        for key in ["DisplayAttributes", "IOMFBServiceUUID", "DisplaySerialNumber", "DisplayProductID",
                    "DisplayVendorID", "IODisplayPrefsKey", "AppleDisplayType", "IOClass", "DisplayPID", "DisplayVID"] {
            if let v = IORegistryEntryCreateCFProperty(node, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() {
                var s = "\(v)".replacingOccurrences(of: "\n", with: " ")
                if s.count > 220 { s = String(s.prefix(220)) + "..." }
                interesting.append("\(key)=\(s)")
            }
        }
        if !interesting.isEmpty || depth == 0 {
            print("   [\(depth)] \(name)")
            for i in interesting { print("        \(i)") }
        }
        var parent: io_service_t = 0
        if IORegistryEntryGetParentEntry(node, "IOService", &parent) != KERN_SUCCESS { IOObjectRelease(node); break }
        IOObjectRelease(node)
        node = parent
    }
}
IOObjectRelease(iter)
