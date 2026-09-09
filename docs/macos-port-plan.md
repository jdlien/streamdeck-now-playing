# macOS Port Plan

Companion to the README, which stays the Windows design document. This file
covers only what changes to run on macOS. Where this file says "README §5.3" it
means that section of the main README.

Status: **revision 5 — the port is complete, 2026-09-09.** All milestones done
and confirmed on the hardware. What follows is the record of how it was
established, kept because the private-API findings need re-checking after every
macOS release.

Previously: **revision 4 — spikes done, M0–M2 implemented 2026-09-09.**
The plugin now builds, loads and runs on macOS; volume works on devices that
have a software volume. §0 has the spike results, §10 the external review that
shaped revision 2.

**Implementation status**

| Milestone | State |
| --- | --- |
| M0 cross-platform refactor | **Done.** 144 portable tests pass on macOS; Windows half still compiles. |
| M1 plugin host on macOS | **Done.** Loads in the Stream Deck app: `[com.jdlien.now-playing] Plugin connected`. |
| M2 Volume | **Done, awaiting hardware test.** Verified end to end against CoreAudio. |
| M3 SD Brightness | **Cut** (S2), and the removal done: hidden by manifest, kept out of the Display Brightness cycle, and dropped from the property inspector on macOS. |
| M4 Display Brightness | **Done, awaiting hardware test.** Both backends verified live on all three displays. |
| M5 Packaging | **Done.** Notarization accepted; Gatekeeper reports `source=Notarized Developer ID`. S4 run: signed, hardened, self-contained builds launch correctly. `build/package-macos.sh` publishes, signs and packs. Notarization needs a stored credential (one command, below). |
| M6 Now Playing | **Done and confirmed on the hardware.** MediaRemote via an Apple-signed host for breadth, AppleScript for Music.app, routed by owning bundle id. |

Two faults worth remembering, both found by testing rather than review:

- **A use-after-free in the DDC path.** `Rebind` released every AV service it
  enumerated, including the ones live backends were still writing through. It
  did not crash: `IOAVServiceWriteI2C` returned `0x10000003`, which reads like
  an ordinary I2C failure. The channel now owns its AV service and releases it
  only after its worker has stopped.
- **The Windows monitor-wake bug is not inherited.** On Windows this plugin's
  periodic DDC read kept monitors from ever staying asleep. Backends now declare
  whether a read costs a bus transaction; DDC displays are never polled while
  idle, and `CGDisplayIsAsleep` gates every transaction, so a sleeping panel is
  left alone entirely. Apple displays answer locally and are still polled.

A useful discovery while starting M0: `EnableWindowsTargeting` lets the
Windows-targeted projects **compile** on macOS, so one checkout keeps both
platforms honest. It is set for non-Windows hosts in `Directory.Build.props`.
It does not make them runnable, which is why the Windows-only tests moved to
their own project rather than being deleted.

## 0. Spike results — read this first

| Spike | Outcome | Consequence |
| --- | --- | --- |
| **S1** MediaRemote via Apple-signed host | **PASS, with one exception** | Now Playing promoted from "conditional v0.7" to viable, event-driven, broad coverage. Music.app needs a fallback. |
| **S2** HID coexistence | **FAIL — hard block** | SD Brightness action is **cut** on macOS. Deck target removed from Display Brightness. |
| **S3** Display write | **PASS** | Both backends write and confirm. D6 validation proven necessary. |

**S1 — the important one.** The same probe dylib, loaded into two different host
processes while QuickTime played **[measured]**:

```
host: mrhost (our own unsigned binary)   RESULT: 0 keys,  isPlaying = NO
host: perl   (Apple-signed system binary) RESULT: 7 keys, isPlaying = YES
    kMRMediaRemoteNowPlayingInfoDuration      = 600
    kMRMediaRemoteNowPlayingInfoElapsedTime   = 0
    kMRMediaRemoteNowPlayingInfoPlaybackRate  = 1
    kMRMediaRemoteNowPlayingInfoTimestamp     = 2026-09-09 18:49:36 +0000
    kMRMediaRemoteNowPlayingInfoMediaType     = ...TypeAudio
    kMRMediaRemoteNowPlayingInfoAssetURL / ContentItemIdentifier
```

Identical code; the only variable is the host's identity. Everything the design
needs works from the Apple-signed host **[measured]**:

- **App identity** — `MRMediaRemoteGetNowPlayingClient` +
  `MRNowPlayingClientGetBundleIdentifier` → `com.apple.QuickTimePlayerX`.
- **Change notifications** — `MRMediaRemoteRegisterForNowPlayingNotifications`
  fired 6 events during the test. **Event-driven, not polling**, so README §5.5's
  architecture survives.
- **Transport commands** — `MRMediaRemoteSendCommand(togglePlayPause)` accepted
  *and verified by effect*: playback rate 1 → 0 → 1, elapsed time advancing.
- **Playback rate** is reported, which answers the 2× playback problem.

**The Music.app exception [measured].** With Music.app owning the session:

```
MRMediaRemoteGetNowPlayingClient          -> ok, bundle = com.apple.Music
MRMediaRemoteGetNowPlayingApplicationIsPlaying -> ok, YES
MRMediaRemoteGetNowPlayingInfo            -> TIMED OUT after 30s, callback never fired
```

It **hangs**, it does not fail. QuickTime worked immediately before and after, so
MediaRemote itself is healthy — this is specific to Music.app. Two consequences,
both now baked into the design: every MediaRemote call needs a **timeout and must
never block a dial**, and Music.app needs the AppleScript adapter, which is the
richest source for it anyway. `GetNowPlayingClient` works even for Music, so it
is a reliable **router**: read the owning bundle ID first, then choose backend.

**S2 [measured].** Exactly one Elgato HID collection exists
(`usagePage=12 usage=1 feature=32 in=512 out=1024`), and
`IOHIDDeviceOpen(kIOHIDOptionsTypeNone)` returns **`kIOReturnExclusiveAccess`**
while the Stream Deck app runs.

Confirmed by elimination: with the Stream Deck app **quit**, the same probe
opened the device and set brightness to 15%, 100% and 60%, every
`IOHIDDeviceSetReport` returning `success` **[measured]**. So our HID code is
correct and the block is purely arbitration. A ~3 s window exists right after
the app relaunches, before it seizes, during which the open succeeds; polling
every 5 s thereafter showed `exclusiveAccess` indefinitely. Not exploitable.

Why Windows differs: `CreateFileW` negotiates a *share mode*, and both the
Elgato Windows app and this plugin open permissively
(`FILE_SHARE_READ | FILE_SHARE_WRITE`), so the handles coexist and
`HidD_SetFeature` goes through the HID class driver either way. IOKit has no
share mode — one client seizing locks everyone out, with no negotiation. This
is a choice in Elgato's Mac app, not a macOS restriction.

Permissions are **not** a factor: the failure is `kIOReturnExclusiveAccess`, an
arbitration result, not `kIOReturnNotPermitted`/`kIOReturnNotPrivileged`, and
the identical process with identical TCC grants succeeded the moment the app
was closed. Accessibility permission is unrelated to HID device access. The app always runs — it is what launches this
plugin — so this is unconditional. There is no SDK route to brightness either.
**Mitigation: none needed.** The Stream Deck app ships its own built-in
`com.elgato.streamdeck.system.keybrightness` action supporting both Keypad and
Encoder **[measured]**, so macOS users already have native deck brightness; only
our dim/restore toggle is lost.

*Can Elgato's action be forked and reskinned?* No — tested 2026-09-09
**[measured]**. Their action is manifest-only: no `CodePath`, `PrivateAPI: true`,
executed inside the app process. 25 bundled plugins declare `PrivateAPI` and
none ships an executable; no third-party plugin has it. Two forks were built and
installed:

- Reusing their action UUID is refused outright —
  `Plugin conflict: 'com.jdlien.brightfork' (action
  'com.elgato.streamdeck.system.keybrightness' already registered by
  'com.elgato.streamdeck.system.keybrightness')`. The bundled plugin wins.
- A unique action UUID plus `PrivateAPI: true` loads without complaint, but
  `PrivateAPI` is a permission flag, not a dispatch mechanism: the internal
  handler (`ESDKeyBrightnessActionController`) is bound to Elgato's UUID, so a
  third-party UUID has nothing behind it.

Editing the bundled plugin in place would break the app's code signature and be
reverted on every update. Their action uses built-in layout `$B1`; only its
title and icon are customizable through the app UI, not the layout, fonts or
colours.

**The real fix is Elgato's**: their Mac app seizes the HID device where the
Windows app opens permissively. Opening non-seizing would make this plugin's
existing, verified-correct brightness code work unchanged. Worth filing as a
feature request; keep the code shipping on Windows and merely hidden on macOS so
that flipping it back on is a manifest change.

**S3 [measured].** Writes confirmed by readback on both backends, then restored:

```
Studio Display XDR (DisplayServices)  0.8 -> 0.55 CONFIRMED -> restored 0.8
Studio Display     (DisplayServices)  0.8 -> 0.55 CONFIRMED -> restored 0.8
BenQ MA270S        (DDC/CI)            63 -> 38   CONFIRMED -> restored 63
```

D6's validation earned its place: the two Studio Displays' DDC services were
**correctly rejected**, including the one that previously returned garbage
parsing as "brightness 158".

Evidence convention, used throughout:

- **[measured]** — observed on the target machine on 2026-09-09. Trustworthy.
- **[repo]** — verified by reading this codebase.
- **[unverified]** — plausible, argued from documentation or third-party
  projects, *not* confirmed here. Treat as a hypothesis with a spike attached.

---

## 1. Goal and scope

Ship the same five actions on macOS: Now Playing (dial), Now Playing Key,
Volume, SD Brightness, Display Brightness.

After the spikes (§0), the split is different from what revision 2 assumed:

- **Now Playing works**, via MediaRemote hosted in an Apple-signed process, with
  a per-app fallback for Music.app. Event-driven, broad coverage.
- **SD Brightness is dead on macOS.** The Stream Deck app holds the HID device
  exclusively and always runs. Elgato's own built-in Brightness action covers
  the need natively, so this is a small loss.

Revised releases:

- **v0.6 — Volume + Display Brightness.** No hardware unknowns left; both
  backends are proven end to end.
- **v0.7 — Now Playing.** Larger, but no longer a research project.
- **Cut: SD Brightness.** Four shipping actions on macOS, not five.

Non-goals, decided up front:

- **Software volume for hardware-only interfaces.** The SSL 2 Mk II exposes no
  software volume to CoreAudio at all **[measured]**. That is the device's
  design, not a bug to route around. What matters is that the dial works
  normally when the default output *does* expose volume — the Studio Display
  XDR speakers being the daily case — and degrades honestly when it does not,
  switching between the two live. See D4.
- **Intel Macs.** Apple Silicon only, tested. The DDC path in §3.4 is
  Apple-Silicon-specific; Intel needs the older `IOFramebuffer` route, not
  planned. Ship arm64-only unless M6 shows a universal binary is genuinely
  cheap — an untested Intel slice is a support liability, not a free win.

---

## 2. Verified environment

Measured 2026-09-09 on the target machine.

| Item | Finding |
| --- | --- |
| macOS | 26.5.2 (build 25F84), Apple M2 Max, arm64 |
| Stream Deck app | 7.5.1 — same version as the Windows box, so no app-version gap |
| .NET SDK | 10.0.301 at `/usr/local/share/dotnet` |
| Swift | 6.3.3, for probes and any helper binary |
| Displays | Studio Display XDR (main), Studio Display (2022), BenQ MA270S |
| Default audio out | SSL 2 Mk II — hardware-only volume, no software control |
| Stream Deck hardware | Stream Deck Plus, USB 0fd9:0084, `MaxFeatureReportSize` 32 — matching the Windows constants. Connected and tested in S2. |

### 2.1 Probe results

The probe sources live in `tools/macos-probes/` with a README; they are kept
because these results need re-checking after every macOS update. Build products
are git-ignored.

**StreamDeck-Tools 7.0.0 on a platform-neutral `net10.0` TFM** **[measured]** —
built and ran on arm64:

```
OS: macOS 26.5.2   Arch: Arm64
  type OK: SDWrapper, Logger, EncoderBase, KeypadBase, ISDConnection, Tools,
           PluginActionIdAttribute
  ISDConnection.SetFeedbackAsync / SetFeedbackLayoutAsync / SetImageAsync
                / SetSettingsAsync / ShowAlert: all present
  Skia PNG encode: 3131 bytes  (typeface: Helvetica)
```

**CoreAudio output devices** **[measured]** — capability is per device, not per
system:

```
BenQ MA270S                 virtualMain=false  perChannel=false  mute=false
Studio Display XDR Speakers virtualMain=true (0.5954)            mute=true
Studio Display Speakers     virtualMain=true (0.6450)            mute=true
SSL 2 Mk II                 virtualMain=false  perChannel=false  mute=false
MacBook Pro Speakers        virtualMain=true (0.6398)            mute=true
Microsoft Teams Audio       virtualMain=true (0.0)               mute=true
```

**Display brightness** **[measured]** — the two Apple displays and the BenQ
answer different APIs, and neither answers both:

```
DisplayServicesGetBrightness
  display 3 (Apple, main)   rc=0     brightness=0.8      Studio Display XDR
  display 2 (BenQ 0x9d1)    rc=1000  (unsupported)
  display 5 (Apple)         rc=0     brightness=0.8      Studio Display

IOAVServiceReadI2C, DDC/CI get-VCP 0x10
  #1 dispext1  write -> 0xe0114101 (fails)          Studio Display
  #2 dispext2  ==> BRIGHTNESS current=63 max=100    BenQ MA270S
  #3 dispext3  write "success", read -> ed c4 88 9e 7e 19 7f 22 71 e6 c2 15
```

Entry #3 is the important one: a **successful** I2C transaction returning
garbage. See D6.

**MediaRemote, direct call from an ordinary unsigned binary** **[measured]**,
tested twice — once with Music.app playing, once with QuickTime Player playing:

```
dlopen OK
symbol MRMediaRemoteGetNowPlayingInfo found
  MRMediaRemoteGetNowPlayingApplicationIsPlaying: present
  MRMediaRemoteSendCommand: present
  MRMediaRemoteRegisterForNowPlayingNotifications: present
RESULT: callback fired, 0 keys
```

The framework loads, the symbol resolves, the callback fires, the dictionary is
empty. This is the macOS 15.4 restriction, still in force on 26.5.2.

**MediaRemote ObjC classes are present** **[measured]** — resolved via
`NSClassFromString` from inside `osascript`:

```
MRNowPlayingRequest: FOUND     MRContentItem: FOUND
MRClient: FOUND                MRPlayerPath: FOUND
MRNowPlayingClient: FOUND      MRMediaRemote: nil
```

A first attempt to call `MRNowPlayingRequest.localNowPlayingItem` from
`osascript` returned `(null)` while QuickTime was playing, and the JXA harness
then crashed. **That result is inconclusive**, not a refutation: no run loop was
spun for the XPC round trip, and the call sequence did not match the known
working implementations. This is exactly what spike S1 exists to settle.

---

## 3. Component findings

### 3.1 Plugin host, layouts, property inspectors, renderers — portable

StreamDeck-Tools 7.0.0 shipped a cross-platform SkiaSharp surface with all
`System.Drawing` APIs marked obsolete. `ArtRenderer` already draws with Skia.
The plugin's whole StreamDeck-Tools surface is nine protocol-level members, all
confirmed present on the neutral TFM **[measured]**.

Layouts (`layouts/*.json`), property inspectors (`pi/*.html`, sdpi-components
v4) and SVG icons are data, not code, and need no change.

One real defect: `ArtRenderer.cs:298` requests `"Segoe UI"` and falls back to
`SKTypeface.Default`, which on macOS is Helvetica rather than the system UI face
**[repo, measured]**.

### 3.2 System volume — public API, device-dependent availability

`kAudioHardwareServiceDeviceProperty_VirtualMainVolume` ('vmvc') on
`kAudioDevicePropertyScopeOutput`, plus `kAudioDevicePropertyMute`, plus a
change listener, maps onto `IVolumeService` closely. All public API. No
entitlement, no TCC prompt.

The gap versus Windows: WASAPI guarantees every endpoint has a software volume.
CoreAudio does not — two of six output devices here have none **[measured]**.
Volume and mute are also *independent* capabilities: a device can expose a
readable-but-not-settable volume, or volume without mute. `IVolumeService` and
`VolumeSnapshot` have no way to express any of this. See D4.

### 3.3 Stream Deck brightness — untested

Conceptually a straight port. Same 32-byte `03 08 <percent>` HID feature report;
`IOHIDManager` + `IOHIDDeviceSetReport(kIOHIDReportTypeFeature)` replaces
`hid.dll`/`cfgmgr32`/`CreateFileW`. Elgato now documents the brightness command
publicly at `docs.elgato.com/streamdeck/hid/general` **[unverified]**, which is
worth reading before reimplementing from the Windows constants.

**The open question is exclusivity.** On Windows the plugin opens with
`FILE_SHARE_READ | FILE_SHARE_WRITE` and coexists with the Stream Deck app
**[repo]**. If the macOS app opens with `kIOHIDOptionsTypeSeizeDevice`, a second
opener is refused and this cannot ship. Binary, cheap to settle: spike S2.

Secondary unknown: whether HID access triggers an Input Monitoring (TCC)
prompt.

**If S2 fails, two actions are affected, not one.** `DisplayBrightnessRenderer.
FromStreamDeck` hardcodes `Available: true` **[repo]**, and
`DisplayTargets.Resolve` honours an explicit `"streamdeck"` binding regardless
of the include setting **[repo]**. Cutting only the standalone SD Brightness
action would leave the Display Brightness dial able to select a deck target
that silently does nothing.

### 3.4 Display brightness — works, via two private frameworks

Neither display family answers a single API, so both paths are required:

- **Apple displays** — `DisplayServicesGetBrightness` /
  `DisplayServicesSetBrightness` / `DisplayServicesCanChangeBrightness` from
  `DisplayServices.framework`, keyed by `CGDirectDisplayID`. Float 0..1.
  Verified reading both Studio Displays **[measured]**.
- **Everything else** — DDC/CI over `IOAVServiceCreateWithService` +
  `IOAVServiceWriteI2C` / `IOAVServiceReadI2C`, walking the IO registry for
  `DCPAVServiceProxy` entries whose `Location` is `External`. Verified reading
  the BenQ: current 63, max 100 **[measured]**.

Both are private API reached by `dlopen`, the same position MonitorControl and
m1ddc occupy. No admin rights, no TCC prompt. An OS update can break either;
failure is detectable at runtime and the dial can degrade to unavailable.

Identity is the hard part, and worse than revision 1 claimed. The current
binding is the monitor's **friendly name** — `DisplayBrightnessAction.BindTo`
stores it in action settings and `DisplayTargets.Resolve` matches it with a
case-insensitive string compare against the enumerated names **[repo]**.
Identical models therefore already collide on Windows. On macOS the problem
compounds: `DisplayServices` keys by `CGDirectDisplayID`, DDC by an IO registry
entry, and neither is stable across reboots or cable changes. See D8.

### 3.5 Now Playing — no *direct* system-wide API

Direct MediaRemote calls from this plugin's own process return nothing
**[measured]**, and the entitlement is not obtainable by third parties. Ruled
out as universal replacements **[unverified, argued]**:

- `MPNowPlayingInfoCenter` *publishes* your own app's now-playing state.
- `MPRemoteCommandCenter` receives commands *for your own player*.
- MusicKit's `SystemMusicPlayer` concerns Music.app, not a system session list.
- Reflection into private internals does not bypass server-side authorization.

**What revision 1 got wrong** is concluding that per-app AppleScript is
therefore the only route. Two leads were missed, and one flat claim was false.

**Lead A — MediaRemote hosted inside an Apple-signed process.** The theory is
that MediaRemote authorizes on the *calling process's* identity, so an Apple
platform binary (`/usr/bin/perl`, `osascript`) can load and call it where our
own binary cannot. `ungive/mediaremote-adapter` is a maintained project built
on exactly this, offering metadata, artwork, event streaming and transport
commands; fastfetch implemented an `osascript` + `MRNowPlayingRequest` variant
after the 15.4 change **[unverified]**. Partial local support: the
`MRNowPlayingRequest`, `MRContentItem`, `MRClient`, `MRPlayerPath` and
`MRNowPlayingClient` classes all resolve on this machine **[measured]** — so
the surface exists. A first crude call returned null and the harness crashed,
which settles nothing (§2.1). **This is the highest-value unknown in the
project.** Spike S1.

If it holds it is transformative: broad coverage with no per-app adapters and
no per-app Automation consent. The costs are a bundled component plus a
supervised child process, dependence on an undocumented allowance Apple can
revoke, and — importantly — it exposes the *system-selected* player, not a
Windows-style enumeration of all sessions.

**Lead B — per-app local IPC beyond AppleScript.** Revision 1 said apps without
a scripting dictionary are "unreachable at any price". That is false. IINA
embeds mpv and can expose a JSON IPC socket via `input-ipc-server`, supporting
both commands and observed properties **[unverified]**. VLC has an HTTP
interface; foobar2000's beefweb now builds for macOS **[unverified]**. These
cost user setup, so they belong behind an opt-in, but they exist.

**Lead C — CoreAudio process activity, as honest shallow coverage.**
`kAudioHardwarePropertyProcessObjectList` plus `kAudioProcessPropertyIsRunning
Output` and per-process bundle IDs are public API **[unverified]** and can
support a truthful "Chrome: audio active" state. It carries no title, artwork,
duration or transport target, and *running output* is not the same as *audible
media*. It must never be promoted into `PlaybackState.Playing` or used to
fabricate a session.

**Rejected outright:** CoreAudio taps and virtual output devices — capture
consent and routing burden far exceeding the semantic value, since samples
carry no metadata. Spotify Web API as a default — OAuth, network dependency,
and a five-user development-mode cap make it unshippable in a Marketplace
plugin.

**A browser extension** remains viable but is a separate product-sized
integration: `navigator.mediaSession` is page-scoped with no browser-wide
enumeration and no general getter for registered handlers, so "next track"
needs per-site work, plus native-messaging host registration and separate
Safari distribution **[unverified]**. Revisit only if browser coverage is still
the dominant gap after S1.

Whatever wins, four properties of the Windows design change and must be
designed for rather than assumed (see D9): universality, event-driven updates,
OS-provided session ranking, and per-app consent.

---

## 4. Decisions

**D1. Split the target frameworks; do not multi-target the whole solution.**
Pure logic moves to plain `net10.0`; Windows implementations keep the Windows
TFM; macOS implementations get their own project. The plugin host multi-targets.
Rejected: `#if WINDOWS` inside shared files, which would put two platforms'
P/Invoke in one translation unit.

**D2. Platform code sits behind interfaces, resolved once at startup.**
`IMediaSessionService` and `IVolumeService` are the model. `NowPlaying.Device`'s
classes get equivalents — note these are *stateful services* with scheduling and
recovery behaviour, not four inert static helpers, so the extraction must
preserve that behaviour, not just the signatures. The hubs are the only
construction sites and become the factories.

**D3. Prefer in-process P/Invoke; allow a helper process where the API demands
it.** Revised from revision 1's blanket prohibition. CoreAudio, IOKit HID, IOKit
AV and DisplayServices are plain C entry points reachable with
`LibraryImport`/`NativeLibrary.Load`. Two carve-outs:

- If S1 succeeds, the MediaRemote host **is** a child process by construction.
  That is the price of the capability, and README decision 2's "no IPC" applies
  to the plugin shell, not to an inherently out-of-process capability.
- If DDC can wedge indefinitely, process isolation becomes a reliability
  decision. A managed timeout around a synchronous native call stops *waiting*;
  it does not cancel the call or free the thread.

Also: `AudioObjectAddPropertyListenerBlock` takes an Objective-C **block**, not
a C function pointer — plain delegate marshalling will not do. Use the non-block
`AudioObjectAddPropertyListener` where possible; otherwise construct, retain and
release the block explicitly. Every native callback needs defined lifetime,
dispatch scheduling, CF/IOKit ownership and shutdown rules: a callback arriving
after disposal takes the whole plugin down.

**D4. Volume capability is a set of flags, not one boolean.** Revision 1's
single `Available` conflated three things. `VolumeSnapshot` gains `HasDevice`,
`CanSetVolume` and `CanMute`, with the level nullable when unreadable. The
renderer shows an unavailable state; the dial rejects unsupported input with
`ShowAlert` rather than offering a broken press. The service re-evaluates on
`kAudioHardwarePropertyDefaultOutputDevice` changes so switching from the SSL 2
to the XDR speakers makes the knob live again without a restart — the
user-visible shape of the §1 non-goal. Rebinding carries a **generation
counter** so late callbacks from the previous device cannot overwrite the new
snapshot.

**D5. Two display-brightness backends behind one target abstraction, with
re-probing.** Choose per display: try `DisplayServicesCanChangeBrightness`,
fall back to DDC, mark unavailable if neither answers. Probe off the input path.
Crucially, a failed first probe may mean *asleep*, not *unsupported* — enumerate
connected displays independently of successful brightness reads, keep
temporarily unavailable targets in the list, and re-probe after wake, topology
change and bounded backoff.

**D6. Validate every DDC reply before trusting or normalizing it.** A successful
`IOAVServiceReadI2C` can return garbage (§2.1 entry #3 — naively parsed, that is
brightness 158). Required before use: source address, the echoed VCP opcode
equals the one requested, the DDC result/status code indicates success, a sane
length byte, a valid non-zero maximum, current ≤ maximum, and a matching XOR
checksum. Validate *before* clamping — clamping nonsense to 100% would hide a
parser defect. A checksum-valid "unsupported VCP" reply is still a failure. Keep
requested brightness and confirmed brightness separate: an accepted I²C write
does not prove the monitor moved.

**D7. Ship macOS without Now Playing first.** See §1.

**D8. Display identity is four separate concepts.** Replacing the current
friendly-name string (§3.4). Model explicitly: the **label** shown to the user;
the **persistent hardware identity** used for settings; the **ephemeral**
`CGDirectDisplayID`/IOKit handle; and **connection/topology** information to
disambiguate duplicates. EDID serials can be absent or duplicated, so a plain
vendor/model/serial join is not enough — MonitorControl uses scored matching
with one-to-one assignment for this reason **[unverified]**. Settings migration
must preserve existing Windows values and unknown platform-specific keys, and an
unresolved explicit binding must **never** silently rebind to a different
screen; it degrades to unavailable.

**D9. The macOS media coordinator needs distinctions the Windows one never
made.** If M7 proceeds, the model must separate: **app identity vs session
identity** (browser profile/tab/frame, IINA instance, and provider must not
collapse into one bundle ID); **observation vs control** (metadata source,
sample timestamp, and whether a command targets an exact session or the global
player); **absent vs unavailable** (no media, permission denied, adapter
disconnected, unsupported, stale are different states); and **confirmed vs
inferred** (audio activity must never fabricate metadata). A targeted command
that fails must not silently fall back to a global command that could hit a
different app. `SessionRanking`'s status ordering can be reused, but its
`IsCurrent` and OS-order inputs no longer exist and need an explicit macOS
policy with defined tie-breaking, stickiness and recency.

---

## 5. Spikes — before M0

Revision 1 put the largest refactor first and the feasibility tests after it.
That is backwards: each of these can change scope, and all are cheap. Time-box
each; the deliverable is a written answer in this document, not code to keep.

**S0 — Land the probes.** Move the §2.1 probe programs into
`tools/macos-probes/` with a README recording invocation, launch context and
observed output. Everything below adds to them.

**S1 — MediaRemote via an Apple-signed host.** ✅ **DONE — PASS** (§0).
Evaluate `ungive/mediaremote-adapter` and the fastfetch `osascript` variant.
Test metadata and *each command* independently — metadata access does not prove
transport works. Test under the final signing and launch arrangement, not just
from a terminal. Cover Music, Podcasts, IINA, Safari and Chrome; empty state
versus failed provider; and wake recovery. **Outcome decides M7's entire shape.**

**S2 — HID coexistence.** ❌ **DONE — FAIL** (§0). Ran on a connected Stream Deck Plus (0x0fd9:0x0084). Enumerate
`0xfd9` filtered by product ID *and* HID collection, not vendor alone. Open with
`kIOHIDOptionsTypeNone` while the Stream Deck app runs. Set **two visibly
different levels and then restore** — the deck cannot report its brightness
(README §5.10), so a successful report submission proves nothing on its own.
Exercise keys, dials, image updates, app-initiated brightness changes,
sleep/wake, unplug/replug, and both launch orders. Record any TCC prompt.
Gates M3 and the deck target inside M5.

**S3 — Display write.** ✅ **DONE — PASS** (§0). Identity across reboot/re-cabling is still open. Write to
the BenQ over DDC and to a Studio Display over DisplayServices, with readback.
Then test identity across a reboot and a cable swap to see what survives, which
sizes the D8 work.

**S4 — Signed packaged launch.** ✅ **DONE — PASS, with two traps.**
A Developer ID-signed, hardened-runtime, self-contained build launches under the
Stream Deck app and connects normally **[measured]**. The JIT entitlements are
required: `allow-jit`, `allow-unsigned-executable-memory` and
`disable-library-validation` are in `build/entitlements.plist`. The traps, both
of which present identically as `Process stopped (terminated)` followed by
`Plugin is unstable and was disabled`, with no plugin log and no crash report:

- **A quarantine attribute anywhere in the payload is fatal.** macOS blocks the
  unnotarized binary; the user sees Gatekeeper's "cannot be opened" dialog while
  the app just reports a terminated process. `xattr -cr` after publishing, and
  notarize for distribution.
- **Framework-dependent publishes do not work**, even unquarantined and signed.
  They run from a terminal but not when the Stream Deck app spawns them, since
  that environment does not lead the apphost to a shared runtime. Self-contained
  is mandatory, which also matches "end users do not need .NET installed"
  (README decision 5). Cost: 102 MB payload versus 19 MB. A self-contained .NET binary with Skia,
signed, hardened-runtime, notarized, quarantined, and launched *by the Stream
Deck app* — not developer-linked from a directory. Single-file extraction has
historically fought the hardened runtime, and non-AOT .NET needs the JIT
entitlement **[unverified]**. Finding this out at M6 would be expensive.

---

## 6. Milestones

Same rule as README §10: each has an exit test, and the next does not start
until it passes.

### M0 — Cross-platform refactor (no behaviour change)

Roughly 1,900 of ~6,900 lines are genuinely platform-bound; the rest is portable
today and merely trapped behind a Windows TFM.

1. Split `NowPlaying.Media` into portable (`net10.0`: snapshot model,
   `SessionRanking`, `TimelineMath`, `MetadataNormalizer`, `ResumeCorrector`,
   `PlaybackState`, `SessionStatus`, `Artwork`, `IMediaSessionService`) and
   `NowPlaying.Media.Windows` (`MediaSessionService`, `MediaSessionProbe`).
   `MediaSessionService.IsSignificantChange` is pure and moves to the portable
   half. Note `MetadataNormalizer` contains a *Windows* Apple Music workaround
   **[repo]** — making it portable does not make applying it to every provider
   correct; gate it per provider.
2. Split `NowPlaying.Audio`: interface and `VolumeSnapshot` portable,
   `VolumeService` (NAudio) into `NowPlaying.Audio.Windows`.
3. Split `NowPlaying.Device`. Extract interfaces preserving the stateful
   services' scheduling and recovery behaviour (D2). `BrightnessState` and
   `MonitorConfiguration.ToPercent` are pure and move to the portable half.
   Place `DisplayBrightnessSnapshot`, service options and lifecycle contracts
   explicitly.
4. **Repair the CLI.** `NowPlaying.Media.Cli` constructs `MediaSessionService`
   directly and makes nine-plus direct calls into `StreamDeckHid` and
   `MonitorConfiguration` **[repo]**. It must move to the new interfaces or the
   solution will not build, even though its macOS port is deferred.
5. `NowPlaying.Plugin` multi-targets `net10.0-windows10.0.19041.0;net10.0`, with
   Windows implementation projects referenced only under the Windows TFM.
   **Fix the output paths first**: the project forces Debug output into one flat
   directory and suppresses the framework and RID suffixes **[repo]**, so
   multi-targeting as written would have both targets overwrite the same
   `NowPlaying.dll` and dependency files. Use per-TFM/per-RID directories, and
   condition the Windows-specific properties as well as the references.
6. Test project retargets to plain `net10.0`. The one live-hardware test is
   `BrightnessTests.cs:83` (`StreamDeckHid.FindDevicePaths()`) **[repo]**.

**Exit test:** the Windows plugin builds, links and runs on the device with no
functional change, and the CLI still works. Portable tests pass on both OSes.
**Equal test counts are not sufficient** — a neutral test project resolves the
neutral target on Windows too, so it would not exercise the Windows
implementations at all. Keep a separate Windows-target integration test pass
that actually constructs the Windows services.

### M1 — Plugin host on macOS

1. Manifest: **keep `CodePath`** and add `CodePathWin` / `CodePathMac` as
   overrides — they are not replacements **[unverified]**. Add a mac entry to
   `OS` with `MinimumVersion` **14** (.NET 10 supports macOS 26, 15 and 14
   **[verified]**; revision 1's "12" was wrong). The private APIs in §3.4 may
   justify a narrower tested floor still.
2. Publish for `osx-arm64` into a per-RID directory.
3. Fix the font: request the system UI face on macOS, per-platform family list,
   still falling back to `SKTypeface.Default`.
4. Hide unimplemented actions using **per-action `OS` keys in the manifest**.
   Action visibility comes from the manifest, not from whether C# registers a
   handler — leaving an action visible but unhandled produces a broken tile.

**Exit test:** the plugin loads in the macOS Stream Deck app, a dial shows a
static rendered layout with correct typography, `pluginlog.log` is written, and
no unimplemented action appears in the action list.

### M2 — Volume

Promoted ahead of SD Brightness: it needs no hardware and gives the first real
vertical slice.

1. `NowPlaying.Audio.Mac`: default-output lookup, `'vmvc'` get/set,
   `kAudioDevicePropertyMute` get/set, listeners for both, plus a listener on
   `kAudioHardwarePropertyDefaultOutputDevice` to rebind. Non-block listener API
   where possible (D3).
2. Implement D4: capability flags, generation counter, unavailable visual state,
   `ShowAlert` on unsupported input.
3. `VolumeRenderer` gains the unavailable state. Pure and unit-testable.

**Exit test:** with the XDR speakers as default output, the dial adjusts volume
and an independent readback agrees — **the volume HUD is not a valid oracle**,
since setting a CoreAudio property need not summon it; check the menu-bar slider
and a fresh property read. Press mutes and unmutes. Changing volume from the
menu bar updates the dial within a second. With the SSL 2 as default output the
dial shows unavailable and rejects input without throwing. Switching default
output between the two while running flips the dial within a second, with no
stale callback from the old device overwriting the new state. Also cover device
disappearance, CoreAudio restart and failed listener registration.

### M3 — SD Brightness — **CUT** (S2 failed)

Not implemented on macOS. `IOHIDDeviceOpen` returns `kIOReturnExclusiveAccess`
while the Stream Deck app runs, and it always runs (§0). Instead, the work here
is *removal*, so nothing on macOS offers a control that silently does nothing:

1. Mark the `com.jdlien.now-playing.brightness` action Windows-only via a
   per-action `OS` key in the manifest.
2. Remove the deck as a Display Brightness target on macOS: the hardcoded
   `Available: true` in `DisplayBrightnessRenderer.FromStreamDeck` and the
   unconditional `"streamdeck"` branch in `DisplayTargets.Resolve` **[repo]**
   must both become platform-aware, or an existing profile synced from Windows
   will offer a dead target.
3. Note Elgato's built-in `com.elgato.streamdeck.system.keybrightness` action in
   the listing as the macOS alternative.

**Exit test:** on macOS neither the SD Brightness action nor a deck target
appears; a profile created on Windows that binds a dial to the deck degrades to
unavailable rather than silently failing. On Windows, nothing changes.

### M4 — Display Brightness *(gated by S3)*

1. `DisplayServices` backend keyed by `CGDirectDisplayID`.
2. DDC backend over `IOAVService` with full D6 reply validation.
3. Enumeration pairing each display with its `DCPAVServiceProxy`, backend chosen
   and re-probed per D5.
4. Implement D8's identity model and settings migration.
5. **Per-display failure isolation.** The current service runs one worker for
   all displays **[repo]**, so a wedged BenQ transaction would block Apple-display
   control too. There is also an existing recovery gap: when one display fails
   during binding while another succeeds, the "no channels" retry is not
   scheduled, so the failed display can stay missing until an unrelated refresh.
   Fix both here rather than porting them. Wire real macOS wake and
   display-reconfiguration notifications.
6. Revalidate whether zero brightness and the existing dim/restore behaviour are
   right for Apple displays.

**Exit test:** a dial bound to the BenQ changes its brightness; one bound to a
Studio Display changes its; cycling walks all three displays plus the deck (if
M3 shipped). A sleeping or disconnected display degrades to unavailable rather
than hanging *any* dial. No display ever reports a level derived from an
unvalidated reply — test with the recorded garbage frame plus synthetic
checksum-valid error replies, wrong-feature replies, truncated frames, zero
maximum and out-of-range values. Identity survives reboot, cable and port
changes, and a changed primary display; **the wrong monitor is never controlled**.

### M5 — Packaging and release of v0.6 *(informed by S4)*

- arm64-only unless a measurement justifies universal. If universal: native
  sidecars must have both slices, merged output must be **re-signed**, and both
  slices validated.
- Code sign with Developer ID, hardened runtime, JIT entitlement, notarize.
- `.sdignore` per README §10.6. **Do not exclude `libSkiaSharp.dylib`** — it is
  a runtime dependency, not a debug artifact like the `.pdb` that motivated the
  rule. Exclude `.dSYM` bundles instead.
- One `.streamDeckPlugin` carrying both platforms' binaries.
- Treat Marketplace policy and Gatekeeper launch behaviour as **separate**
  questions; no explicit notarization mandate was found in Elgato's current
  distribution docs **[unverified]**.

**Exit test:** installs from a genuinely downloaded, quarantined package on a
Mac with no developer tools and no .NET runtime, launched by the Stream Deck
app; passes README §11 for the shipping actions; an upgrade preserves settings;
native dependencies are present and signatures intact. Measure resources for the
plugin **and any helper**: idle polling, callback and handle counts, bounded
retries, clean process-tree shutdown.

### M6 — Now Playing on macOS (v0.7) — shape settled by S1

The architecture S1 produced is a **router**, not a pile of adapters:

```
MRMediaRemoteGetNowPlayingClient  ──>  owning bundle ID   (works for every app,
                                                           including Music)
        │
        ├── bundle == com.apple.Music ──> AppleScript adapter
        │                                 (MediaRemote's info call hangs here)
        └── everything else ───────────> MediaRemote info + notifications
```

1. **The MediaRemote host.** A small bundled dylib loaded into an Apple-signed
   host process, supervised by the plugin. Loading alone is enough to run it —
   a constructor fires on load — so the host needs no cooperation. It streams
   snapshots and artwork out, and takes transport commands in. Evaluate
   `ungive/mediaremote-adapter` before writing our own; its licence and
   maintenance status decide build-vs-adopt.
2. **Hard timeout on every MediaRemote call.** The Music.app hang (§0) is the
   proof: calls do not fail, they never return. No dial may ever block on one.
   A call that exceeds its deadline marks that provider stale and falls through
   to the router's other branch.
3. **The Music.app adapter** via AppleScript, whose dictionary carries
   everything needed — `playpause`, `next track`, `previous track`,
   `player position`, `player state`, `duration`, `artist`, `name`, and an
   `artwork` class **[measured]**.
4. Optional opt-in adapters where they add capability the router cannot reach:
   IINA via mpv IPC, foobar2000 via beefweb (§3.5 Lead B). Not v0.7 scope.
4. Artwork cached by track identity (`persistent ID` for Music) — fetching per
   poll is far too expensive.
5. AppleScript queries need deadlines, bounded concurrency, and must **not
   launch an app merely by probing it**. One hung app must not delay the others;
   late results and artwork are discarded after a track or session change.
6. "Poll-only" overstates it: Music and Spotify emit distributed notifications
   that can trigger a query, with polling retained for reconciliation
   **[unverified]**. These are per-app conventions, not a universal bus.
7. **Timeline work the Windows model lacks**: the snapshot extrapolates at 1×,
   but podcasts and video play at 1.5× or 2×, so playback rate must enter the
   model. Poll-derived positions need an explicit sample timestamp and staleness
   expiry.
8. TCC: detect "not authorized" and explain it in the property inspector rather
   than letting the dial silently do nothing.
9. Give the CLI a macOS build **early** — it was what made the Windows media
   work tractable (README §10.1) and the same applies here.

**Exit test:** Music playing shows title, artist, art and progress; the dial
skips and toggles. Two playing apps rank correctly; a pinned paused app while
another owns the global session behaves per policy; two browser tabs do not
collapse into one session; a selected app that exits immediately before a
command fails cleanly without hitting a different app. Hung queries, permission
denial and revocation, provider restart, and stale results after a track change
all degrade comprehensibly. Timeline correct across seek, buffering, live
stream, 2× playback, long pause and sleep/wake.

---

## 7. Effort shape

Relative size and confidence, not calendar estimates.

| Item | Size | Confidence |
| --- | --- | --- |
| S0–S4 spikes | Small each | They *are* the measurements |
| M0 refactor | Largest single chunk; mechanical | High |
| M1 host | Small | High — already proven |
| M2 Volume | Small–medium | High |
| M3 SD Brightness | Small | Blocked on S2 |
| M4 Display Brightness | Medium; identity is the risk | Medium |
| M5 Packaging | Medium; notarization is new ground | Medium |
| M6 Now Playing | Large; shape unknown until S1 | Low |

---

## 8. Risks

1. **HID exclusivity.** Binary; gates M3 and part of M4. Spike S2.
2. **MediaRemote host allowance.** If S1 succeeds, the capability rests on an
   undocumented Apple allowance that can be revoked in any release. Design M6 so
   losing it degrades to per-app adapters rather than breaking the plugin.
3. **Private framework breakage.** `DisplayServices` and `IOAVService` can change
   in any macOS release. Detectable; degrade to unavailable.
4. **Display identity.** No documented `CGDirectDisplayID` ↔ `DCPAVServiceProxy`
   mapping. Fallback: one-time user identification in the property inspector —
   but that cannot stay trustworthy if enumeration order later changes, so it
   needs a stable persisted key regardless.
5. **Settings compatibility.** `DisplayTargets` identity changes shape; a profile
   edited on macOS must not corrupt the same profile on Windows, and unknown
   platform keys must survive a round trip.
6. **Notarization and single-file.** Unknown interaction between self-contained
   .NET, `PublishSingleFile`, hardened runtime and launch as a Stream Deck child
   process. Spike S4.
7. **Trimming.** README decision 7 already flags three trim warnings on Windows;
   macOS adds a second untested trimming target.
8. **Blocking native calls.** A wedged DDC transaction on a single worker thread
   can stall unrelated actions (§6 M4.5).

---

## 9. Open questions

1. **Does MediaRemote work from an Apple-signed host process?** The single
   highest-value question in the project. Spike S1.
2. **Does the Stream Deck app seize the HID device on macOS?** Spike S2.
3. **What display identity survives reboot and re-cabling?** Spike S3.
4. **Universal binary or arm64-only?** Depends on M5's size measurement.
5. **Does Elgato require notarization for macOS Marketplace plugins?** README
   §13 read the guidelines from a Windows perspective.
6. **Is browser coverage still the dominant gap after S1?** Decides whether the
   extension in §3.5 is ever worth starting.

---

## 10. Review log

Revision 2 incorporates an external review of revision 1 (codex, 2026-09-09).
Claims were re-verified here before adoption; the review is credited, not
trusted blindly.

**Adopted after verifying in this repo:**

- Display binding is the monitor's **friendly name**, not the GDI name revision
  1 claimed — and identical models already collide (§3.4, D8).
- `DisplayBrightnessRenderer.FromStreamDeck` hardcodes `Available: true`, so an
  S2 failure breaks two actions, not one (§3.3, M3).
- `DisplayBrightnessService` runs a single worker for all displays, so one
  wedged monitor blocks the others; plus an existing binding-failure recovery
  gap (M4.5).
- `NowPlaying.Media.Cli` calls Windows device statics directly and would break
  in M0 (M0.4).
- The plugin's flat Debug `OutDir` with suppressed TFM/RID suffixes would make
  multi-targeted builds overwrite each other (M0.5).

**Adopted after verifying externally:**

- .NET 10 supports macOS 26, 15 and 14 — revision 1's `MinimumVersion: "12"` was
  wrong (M1.1).

**Adopted on argument, flagged [unverified]:**

- Manifest `CodePath` is retained alongside the platform overrides; per-action
  `OS` keys are the right way to hide unimplemented actions (M1).
- `AudioObjectAddPropertyListenerBlock` takes an ObjC block, not a C function
  pointer (D3).
- Splitting volume capability into `CanSetVolume` / `CanMute` (D4).
- Stronger DDC validation, and separating requested from confirmed (D6).
- `libSkiaSharp.dylib` must not be excluded as a debug artifact (M5).
- Exit tests needed real bounds; several were too weak to catch failure.

**The substantive correction:** revision 1 concluded per-app AppleScript was the
only remaining route for Now Playing. Two leads were missed — MediaRemote hosted
in an Apple-signed process, and per-app IPC for apps without scripting
dictionaries — and the claim that non-scriptable apps are "unreachable at any
price" was false (IINA disproves it). Now Playing is no longer treated as
settled; S1 decides it.

**Partially checked here, still open:** `MRNowPlayingRequest`, `MRContentItem`,
`MRClient`, `MRPlayerPath` and `MRNowPlayingClient` all resolve on this machine
**[measured]**, so the surface Lead A depends on exists. A first call from
`osascript` returned null and the harness crashed — inconclusive, and precisely
what S1 must settle properly.

**Noted, not yet adopted:** the review recommended a full capability/identity/
lifecycle design pass before M0. D8 and D9 capture the model; whether that
becomes its own milestone depends on S1's outcome, since M6's shape drives most
of it.
