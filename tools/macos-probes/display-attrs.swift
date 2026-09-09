import Foundation
import IOKit

// Find every IOReg node carrying DisplayAttributes, and show its ancestry so a
// DCPEXT index can be tied to an EDID product/serial.
var iter: io_iterator_t = 0
IORegistryCreateIterator(kIOMainPortDefault, "IOService", IOOptionBits(kIORegistryIterateRecursively), &iter)
while case let svc = IOIteratorNext(iter), svc != 0 {
    defer { IOObjectRelease(svc) }
    guard let attrs = IORegistryEntryCreateCFProperty(svc, "DisplayAttributes" as CFString, kCFAllocatorDefault, 0)?
            .takeRetainedValue() as? [String: Any] else { continue }
    var nb = [CChar](repeating: 0, count: 200); IORegistryEntryGetName(svc, &nb)
    print("\nnode: \(String(cString: nb))")
    if let pa = attrs["ProductAttributes"] as? [String: Any] {
        for k in ["ManufacturerID", "ProductID", "SerialNumber", "ProductName", "AlphanumericSerialNumber", "YearOfManufacture"] {
            if let v = pa[k] { print("   \(k) = \(v)") }
        }
    }
    // ancestry, to locate the DCPEXT index
    var node = svc; IOObjectRetain(node)
    for _ in 0..<10 {
        var n2 = [CChar](repeating: 0, count: 200); IORegistryEntryGetName(node, &n2)
        let nm = String(cString: n2)
        if nm.contains("DCPEXT") || nm.contains("dispext") || nm.contains("DCP") { print("   ancestor: \(nm)") }
        var parent: io_service_t = 0
        if IORegistryEntryGetParentEntry(node, "IOService", &parent) != KERN_SUCCESS { IOObjectRelease(node); break }
        IOObjectRelease(node); node = parent
    }
}
IOObjectRelease(iter)
