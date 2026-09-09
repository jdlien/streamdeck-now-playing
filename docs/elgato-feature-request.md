# TODO: file this with Elgato

Not yet submitted. Where to send it: Elgato's Maker/developer support or the
`elgatosf` GitHub org (the SDK repos take issues). Worth attaching the log line
and the two-line repro below — it is a specific, small, verifiable ask.

**Status:** drafted 2026-09-09, not filed.

---

## Title

Stream Deck macOS app seizes the HID device, locking out plugins that the
Windows app permits

## Body

On Windows, a plugin can send HID feature reports to the Stream Deck while the
Stream Deck app is running. Both openers permit sharing, so the handles coexist
and `HidD_SetFeature` goes through the HID class driver:

```c
CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, ...)   // succeeds on Windows
```

On macOS the same plugin cannot open the device at all. `IOHIDDeviceOpen` with
`kIOHIDOptionsTypeNone` — the non-exclusive option — returns
`kIOReturnExclusiveAccess`, because the Stream Deck app has opened the device
with `kIOHIDOptionsTypeSeizeDevice`. IOKit has no share-mode negotiation: one
client seizing locks out every other client unconditionally.

### Repro

macOS 26.5.2, Stream Deck app 7.5.1, Stream Deck Plus (`0x0fd9:0x0084`).

1. With the Stream Deck app running, open the device's only HID collection
   (`PrimaryUsagePage` 12, `MaxFeatureReportSize` 32) with
   `IOHIDDeviceOpen(device, kIOHIDOptionsTypeNone)` → `kIOReturnExclusiveAccess`.
2. Quit the Stream Deck app and repeat → the open succeeds, and
   `IOHIDDeviceSetReport(device, kIOHIDReportTypeFeature, 3, report, 32)` with
   the standard `03 08 <percent>` brightness report returns `kIOReturnSuccess`
   at 15%, 100% and 60%.

The only variable is whether your app is running. Same process, same code
signature, same TCC grants. (There is also a ~3 s window right after the app
launches, before it seizes, during which the open succeeds — which is further
evidence that the seize is the sole cause.)

### The ask

Open the Stream Deck HID device **without** `kIOHIDOptionsTypeSeizeDevice` on
macOS, matching the sharing behaviour your Windows app already allows.

Alternatively — and arguably better — expose device brightness through the
plugin SDK so no direct HID access is needed. Today
`com.elgato.streamdeck.system.keybrightness` is a manifest-only action with
`PrivateAPI: true` executed inside the app, and there is no corresponding
`setBrightness` event in the plugin protocol, so third-party plugins have no
supported route to a capability the app itself offers.

### Why it matters

Plugins that present brightness in their own visual language, or that combine
deck brightness with other controls on one dial, work on Windows and cannot be
ported to macOS. The plugin code is otherwise identical and already verified
correct on macOS — it is blocked purely by device arbitration.

---

## Notes for us

- Our SD Brightness action stays in the codebase and keeps shipping on Windows;
  on macOS it is hidden via a per-action `OS` key. If Elgato changes this,
  re-enabling is a manifest edit, not a rewrite. See
  `docs/macos-port-plan.md` §0 (S2) and milestone M3.
- Reproduction programs: `tools/macos-probes/hid-coexist.swift`,
  `tools/macos-probes/hid-all.swift`.
