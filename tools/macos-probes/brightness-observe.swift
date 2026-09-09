// Confirm the callback shape of DisplayServicesRegisterForBrightnessChangeNotifications.
import Foundation
import CoreGraphics

let ds = dlopen("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", RTLD_NOW)!
typealias RegisterFn = @convention(c) (CGDirectDisplayID, UInt32, @convention(c) (UInt32, CGDirectDisplayID, UnsafeRawPointer?, UnsafeRawPointer?) -> Void) -> Int32
let register = unsafeBitCast(dlsym(ds, "DisplayServicesRegisterForBrightnessChangeNotifications")!, to: RegisterFn.self)
typealias GetFn = @convention(c) (CGDirectDisplayID, UnsafeMutablePointer<Float>) -> Int32
let get = unsafeBitCast(dlsym(ds, "DisplayServicesGetBrightness")!, to: GetFn.self)

var count: UInt32 = 0
CGGetOnlineDisplayList(0, nil, &count)
var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
CGGetOnlineDisplayList(count, &ids, &count)

for id in ids {
    var b: Float = -1
    guard get(id, &b) == 0 else { print("display \(id): not a DisplayServices display, skipping"); continue }
    let rc = register(id, id, { context, display, _, _ in
        var value: Float = -1
        let g = unsafeBitCast(dlsym(dlopen("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", RTLD_NOW), "DisplayServicesGetBrightness")!, to: GetFn.self)
        _ = g(display, &value)
        print("  CALLBACK display=\(display) context=\(context) brightness=\(value)")
    })
    print("registered display \(id) -> rc=\(rc)")
}

print("\nlistening 12s. Change an Apple display's brightness (keyboard, Lunar, anything).")
RunLoop.current.run(until: Date().addingTimeInterval(12))
print("done")
