# macOS probes

Throwaway programs that answer the feasibility questions in
`docs/macos-port-plan.md`. They are kept because their results need re-checking
after every macOS update — §2.1 and §0 of the plan cite them by name.

Nothing here ships. Build commands are in the comment at the top of each source
file. Results as of macOS 26.5.2 (2026-09-09) are recorded in the plan.

| File | Answers |
| --- | --- |
| `hid-coexist.swift` | Can we open the Stream Deck's HID device while the Elgato app runs? (**No** — `kIOReturnExclusiveAccess`.) |
| `hid-all.swift` | Enumerate every Elgato HID collection and try each. (One collection; seized.) |
| `mediaremote-probe.m` | Does MediaRemote return now-playing data? Built as a dylib with a constructor so it runs on load, letting the same code run in different host processes. |
| `mediaremote-control.m` | Beyond metadata: transport commands, change notifications, owning-app identity. |
| `mr-music-diag.m` | Why Music.app hangs `MRMediaRemoteGetNowPlayingInfo` while other apps do not. |
| `mrhost.c` | Minimal unsigned host, the baseline for the MediaRemote comparison. |
| `display-write.swift` | Read *and write* brightness on both backends, with the D6 DDC reply validation. |

## The MediaRemote comparison

The point of `mrhost` versus `/usr/bin/perl` is that the dylib is identical and
only the host process differs:

```sh
clang -dynamiclib -framework Foundation -o mediaremote-probe.dylib mediaremote-probe.m
clang -o mrhost mrhost.c

./mrhost ./mediaremote-probe.dylib                                    # 0 keys
/usr/bin/perl -e 'use DynaLoader; DynaLoader::dl_load_file("'"$PWD"'/mediaremote-probe.dylib", 0x01);'   # real data
```

`display-write.swift` is read-only unless given `--write`, which changes
brightness and restores it.
