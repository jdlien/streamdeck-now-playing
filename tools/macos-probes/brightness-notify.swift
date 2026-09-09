// What does macOS emit when an Apple display's brightness changes? Lunar tracks
// this live, so something is observable; DDC monitors have no such signal.
import Foundation
import CoreGraphics

let ds = dlopen("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", RTLD_NOW)
print("=== DisplayServices notification-ish symbols ===")
for s in ["DisplayServicesRegisterForBrightnessChangeNotifications",
          "DisplayServicesUnregisterForBrightnessChangeNotifications",
          "DisplayServicesRegisterForAmbientLightCompensationNotifications",
          "DisplayServicesBrightnessChanged",
          "DisplayServicesGetBrightness",
          "DisplayServicesSetBrightness",
          "DisplayServicesCanChangeBrightness",
          "DisplayServicesGetLinearBrightness",
          "DisplayServicesSetLinearBrightness"] {
    print("  \(s): \(ds.flatMap { dlsym($0, s) } != nil ? "present" : "absent")")
}

print("\n=== listening to every distributed notification for 12s ===")
print("    (change an Apple display's brightness now)")
var seen = Set<String>()
DistributedNotificationCenter.default().addObserver(
    forName: nil, object: nil, queue: .main) { note in
    let n = note.name.rawValue
    if seen.insert(n).inserted { print("  DISTRIBUTED: \(n)") }
}
// Also CGDisplay reconfiguration, to see whether a brightness change looks like one.
CGDisplayRegisterReconfigurationCallback({ display, flags, _ in
    print("  CGDisplayReconfiguration: display=\(display) flags=0x\(String(flags.rawValue, radix: 16))")
}, nil)
RunLoop.current.run(until: Date().addingTimeInterval(12))
print("=== done ===")
