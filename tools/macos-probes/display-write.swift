// S3 spike: writing brightness. Reading is already proven; this tests writes
// on both backends and restores the original value.
//   swiftc -o display-write display-write.swift && ./display-write [--write]
import Foundation
import CoreGraphics
import IOKit

let doWrite = CommandLine.arguments.contains("--write")

// ---- Apple displays: DisplayServices ----
let ds = dlopen("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", RTLD_NOW)
typealias DSGet = @convention(c) (CGDirectDisplayID, UnsafeMutablePointer<Float>) -> Int32
typealias DSSet = @convention(c) (CGDirectDisplayID, Float) -> Int32
typealias DSCan = @convention(c) (CGDirectDisplayID) -> Bool
let dsGet = ds.flatMap { dlsym($0, "DisplayServicesGetBrightness") }.map { unsafeBitCast($0, to: DSGet.self) }
let dsSet = ds.flatMap { dlsym($0, "DisplayServicesSetBrightness") }.map { unsafeBitCast($0, to: DSSet.self) }
let dsCan = ds.flatMap { dlsym($0, "DisplayServicesCanChangeBrightness") }.map { unsafeBitCast($0, to: DSCan.self) }

var count: UInt32 = 0
CGGetOnlineDisplayList(0, nil, &count)
var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
CGGetOnlineDisplayList(count, &ids, &count)

print("=== DisplayServices backend (Apple displays) ===")
for id in ids {
    guard let dsGet, let dsSet else { break }
    var b: Float = -1
    guard dsGet(id, &b) == 0 else { print("display \(id): not a DisplayServices display"); continue }
    let can = dsCan?(id) ?? false
    print("display \(id): brightness=\(b) canChange=\(can)")
    guard doWrite, can else { continue }
    let target: Float = b > 0.5 ? b - 0.25 : b + 0.25
    let rc = dsSet(id, target)
    usleep(400_000)
    var after: Float = -1; _ = dsGet(id, &after)
    print("   set \(target) -> rc=\(rc), readback=\(after)  \(abs(after - target) < 0.02 ? "CONFIRMED" : "MISMATCH")")
    _ = dsSet(id, b); usleep(400_000)
    var restored: Float = -1; _ = dsGet(id, &restored)
    print("   restored to \(b), readback=\(restored)")
}

// ---- Everything else: DDC/CI over IOAVService ----
let io = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW)!
typealias CreateFn = @convention(c) (CFAllocator?, io_service_t) -> Unmanaged<CFTypeRef>?
typealias RWFn = @convention(c) (CFTypeRef, UInt32, UInt32, UnsafeMutableRawPointer, UInt32) -> IOReturn
let avCreate = unsafeBitCast(dlsym(io, "IOAVServiceCreateWithService")!, to: CreateFn.self)
let avRead = unsafeBitCast(dlsym(io, "IOAVServiceReadI2C")!, to: RWFn.self)
let avWrite = unsafeBitCast(dlsym(io, "IOAVServiceWriteI2C")!, to: RWFn.self)

/// D6: a successful I2C read can still be garbage. Validate before trusting.
func parseVCPReply(_ r: [UInt8], expecting vcp: UInt8) -> (current: Int, max: Int)? {
    guard r.count >= 11 else { return nil }
    guard r[0] == 0x6E else { return nil }              // source address
    guard r[2] == 0x02 else { return nil }              // get-VCP feature reply
    guard r[3] == 0x00 else { return nil }              // result code: success
    guard r[4] == vcp else { return nil }               // echoed opcode must match
    let maxV = Int(r[6]) << 8 | Int(r[7])
    let curV = Int(r[8]) << 8 | Int(r[9])
    guard maxV > 0, curV <= maxV else { return nil }
    var checksum: UInt8 = 0x50                          // 0x6E ^ 0x51 ^ ... per DDC spec
    for b in r[0..<10] { checksum ^= b }
    guard checksum == r[10] else { return nil }
    return (curV, maxV)
}

func readBrightness(_ av: CFTypeRef) -> (Int, Int)? {
    var req: [UInt8] = [0x82, 0x01, 0x10, 0x00]
    req[3] = 0x6E ^ 0x51 ^ req[0] ^ req[1] ^ req[2]
    guard req.withUnsafeMutableBytes({ avWrite(av, 0x37, 0x51, $0.baseAddress!, UInt32($0.count)) }) == KERN_SUCCESS else { return nil }
    usleep(50_000)
    var reply = [UInt8](repeating: 0, count: 12)
    guard reply.withUnsafeMutableBytes({ avRead(av, 0x37, 0x51, $0.baseAddress!, UInt32($0.count)) }) == KERN_SUCCESS else { return nil }
    return parseVCPReply(reply, expecting: 0x10)
}

func writeBrightness(_ av: CFTypeRef, _ value: Int) -> Bool {
    var req: [UInt8] = [0x84, 0x03, 0x10, UInt8(value >> 8), UInt8(value & 0xFF), 0x00]
    req[5] = 0x6E ^ 0x51 ^ req[0] ^ req[1] ^ req[2] ^ req[3] ^ req[4]
    return req.withUnsafeMutableBytes { avWrite(av, 0x37, 0x51, $0.baseAddress!, UInt32($0.count)) } == KERN_SUCCESS
}

print("\n=== DDC/CI backend (non-Apple displays) ===")
var iter: io_iterator_t = 0
IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("DCPAVServiceProxy"), &iter)
var n = 0
while case let svc = IOIteratorNext(iter), svc != 0 {
    defer { IOObjectRelease(svc) }
    let loc = IORegistryEntryCreateCFProperty(svc, "Location" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? String ?? "?"
    guard loc == "External", let um = avCreate(kCFAllocatorDefault, svc) else { continue }
    n += 1
    let av = um.takeRetainedValue()
    guard let (cur, maxV) = readBrightness(av) else { print("service #\(n): no valid DDC reply (correctly rejected)"); continue }
    print("service #\(n): brightness \(cur)/\(maxV)  VALIDATED")
    guard doWrite else { continue }
    let target = cur > maxV / 2 ? cur - maxV / 4 : cur + maxV / 4
    print("   writing \(target)... \(writeBrightness(av, target) ? "accepted" : "REJECTED")")
    usleep(600_000)
    if let (after, _) = readBrightness(av) { print("   readback=\(after) \(abs(after - target) <= 2 ? "CONFIRMED" : "MISMATCH")") }
    _ = writeBrightness(av, cur); usleep(600_000)
    if let (r, _) = readBrightness(av) { print("   restored to \(cur), readback=\(r)") }
}
IOObjectRelease(iter)
