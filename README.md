# Stream Deck Now Playing

Design and implementation plan for a Windows plugin for the Elgato Stream Deck +.
It shows the current Windows media session (title, artist, play state, progress)
on one dial's touch-strip segment and controls playback with that dial.

Status (2026-09-06): milestone 1 is done and verified live; the display,
input, and recovery code for milestones 3 to 5 is written and unit tested; the
plugin is linked into the Stream Deck app in developer mode and its process
connects. What remains needs eyes and hands on the device: place the action
on a dial and run the checks in section 10. Facts below were verified on this
machine on 2026-09-06.

## 1. Requested behavior

Use one dial and its touch-strip segment. Initial placement: the rightmost dial
on the first page.

Display:
- A small play or pause icon in the top-left corner, reflecting playback state.
- Artist on the top line, to the right of the icon.
- Song title on the line below, across the full width. (Originally the other
  way round; swapped 2026-09-06 after the first look at the strip, because the
  title is the line that needs the room and the icon shortens the top row.)
- A playback progress bar underneath the text.
- Elapsed time at the bottom left and track length at the bottom right, in
  small text. Added 2026-09-06 after the first look at the strip.

| Input | Action |
| --- | --- |
| Turn right | Next track |
| Turn left | Previous track |
| Press the dial | Toggle play/pause |
| Tap the touch strip above the dial | Toggle play/pause |

The same media session is used for the displayed information and all commands.

Added 2026-09-06 for Marketplace eligibility and for decks without a dial:

- **Album art.** The session's thumbnail sits behind the play/pause icon on the
  strip (on by default, switchable in the action's settings).
- **Now Playing Key**, a second action for any key: album art with a play or
  pause badge in the corner, the track title in the key's title field, a
  configurable press (play/pause by default, or next, previous, nothing) and
  a configurable hold (next by default).
- **Preferred player**, a global setting shared by both actions: automatic, or
  pin one of the players Windows currently reports.

## 2. Verified environment

| Item | Finding |
| --- | --- |
| Windows | 11, build 26200 |
| Stream Deck app | 7.5.1, installed at `C:\Program Files\Elgato\StreamDeck` |
| Device | Stream Deck + connected (USB 0fd9:0084), firmware 2.0.3.7. The app log shows several USB disconnect/reconnect cycles, so reconnection handling is a real requirement. |
| Toolchains | .NET 10 SDK (plus 9), Node 24, Rust 1.97, Python 3.12, PowerShell 7 |
| Node for plugins | Provided by the Stream Deck app itself. The manifest declares Node 20 or 24 and the app manages runtimes under `%APPDATA%\Elgato\StreamDeck\NodeJS`. Not used by this plugin. |
| Windows media API | Works from an unpackaged process. `tools/Check-MediaSessions.ps1` enumerated the live session. |
| Apple Music (Store) | Registers a session. App id `AppleInc.AppleMusicWin_nzyj5cx40ttqa!App`. Primary test player. |
| foobar2000 2.25.10 | Registers a session natively as `foobar2000.exe`; v2 gained this, no plug-in needed. Measured here on 2026-09-06 (and in `../ak820-pro` with 2.24.6): title, artist, and thumbnail present, next/previous/toggle enabled, but no timeline at all (0 / 0 with a zero `LastUpdatedTime`), so no progress bar. |
| Installed plugins | PilotsDeck (a self-contained .NET exe, 29 MB) is a local example of a non-Node plugin with a custom dial layout. |

Apple Music session observed with the diagnostic script:

- While playing: status `Playing`, type `Music`, rate 1. Shuffle and repeat are
  not reported.
- Title and Artist populated. Artist contained `Michael Oakley — Prologue` (an
  em dash, U+2014; the console rendered it as a hyphen) while AlbumTitle was
  empty and AlbumArtist repeated the Artist value. The suffix is the album
  name: a second track from the same album, `Memory of You` by
  `Michael Oakley & Missing Words — Prologue`, carried the same suffix. So
  Apple Music packs `Artist — Album` into Artist. TrackNumber was 0.
- At launch, before anything is loaded, Apple Music registers a session in the
  `Opened` state and Windows reports it as the current session. Measured in
  `../ak820-pro`; it is why session choice must rank rather than follow
  "current" (see 5.3).
- Thumbnail reference present (album art is available if wanted later).
- Controls: next, previous, and toggle enabled. Seek not enabled.
- Timeline while playing: Position in whole seconds, duration 3:55.
  `LastUpdatedTime` advanced on every sample taken 3 s apart, so Apple Music
  refreshes the timeline at least every few seconds and probably every second.
- Timeline while paused: Position and `LastUpdatedTime` both froze at the
  moment of the pause. Other players are less generous while playing; see 5.4.

foobar2000 2.25.10 session observed while playing, with Apple Music still open
and paused:

- Status `Playing`, type `Music`. PlaybackRate, shuffle, and repeat are not
  reported.
- Title and Artist populated (`Remember (ESCM 12' Mix)` by `BT`), AlbumTitle
  empty, thumbnail present.
- Controls: next, previous, and toggle enabled. Seek not enabled.
- Timeline: Position, StartTime, and EndTime all zero, and `LastUpdatedTime`
  is zero ticks, which displays as 1601-01-01 UTC. This is the "never updated"
  case in 5.4, seen live.
- Windows named foobar2000 the current session, and the ranking rule in 5.3
  picks it too: `Playing` beats Apple Music's `Paused`.
- After its Stop button: status `Stopped`, with title, artist, thumbnail, and
  all three control flags still reported. A stopped foobar2000 still has a
  track loaded, which is why 5.3 keeps `Stopped` as a low-rank candidate.

## 3. Decisions

1. **Language and runtime: C# on .NET 10, Windows-targeted.** The media API is
   a WinRT API. C# has first-class projections with real events and async.
   Node has no usable WinRT binding, so a Node plugin would need a C# helper
   process anyway. Target framework `net10.0-windows10.0.19041.0`; the Windows
   SDK projection is included automatically for Windows-versioned targets.
2. **One process: the plugin shell is also C#, using StreamDeck-Tools 7.0.**
   Version 7.0.0 (April 2026) ships a `net10.0` target and exposes
   `EncoderBase` with `DialRotate`, `DialDown`, `DialUp`, `TouchPress`,
   `SetFeedbackAsync`, and `SetFeedbackLayoutAsync`. One process means no IPC,
   no child-process supervision, one log, one debugger.
   - Fallback: the Stream Deck WebSocket protocol is small (register, a handful
     of events, `setFeedback`). If StreamDeck-Tools' dependencies (Newtonsoft,
     NLog, SkiaSharp, System.Drawing.Common) fight trimming or add too much size,
     replace it with a ~200-line hand-rolled client on `System.Net.WebSockets`
     and `System.Text.Json`.
   - Rejected: official Node SDK plugin plus a C# helper over stdio. Two
     processes for one action is not worth Elgato's templates.
3. **Reusable media component.** `NowPlaying.Media` is a class library with no
   Stream Deck dependency. `NowPlaying.Media.Cli` is a thin console host that
   prints session events as JSON. It is the test harness for milestone 1 and
   the reusable piece for future utilities.
4. **Icon convention: state, as requested.** Play icon while playing, pause icon
   while paused. The strip is a display, not a labelled button, and the dial is
   the control. Swapping to the action convention is a one-line change.
5. **Minimum versions.** Stream Deck 7.1 (`Software.MinimumVersion`), Windows 10
   1809 or later (the media session API arrived in build 17763). End users do
   not need .NET installed because the publish is self-contained.
6. **Identity.** Plugin UUID `com.jdlien.now-playing`, action UUID
   `com.jdlien.now-playing.dial`. Lowercase letters, digits, hyphens, and periods
   only.
7. **Packaging.** `dotnet publish -c Release -r win-x64 --self-contained
   -p:PublishSingleFile=true -p:PublishTrimmed=true`, then `streamdeck pack`
   into a `.streamDeckPlugin`. Measured 2026-09-06, trimmed: `NowPlaying.exe`
   20.7 MB plus `libSkiaSharp.dll` 11.6 MB, which SkiaSharp keeps as a
   separate native file, plus an 84 MB `libSkiaSharp.pdb` that packaging must
   delete. Three trim warnings come from StreamDeck-Tools' dependencies
   (Newtonsoft, NLog); whether the trimmed build runs on the device is an
   open milestone 6 check, with untrimmed as the fallback. SkiaSharp and
   System.Drawing are dead weight here (this plugin never draws an image),
   which is the case for the hand-rolled protocol client in decision 2 if
   size ever matters.

## 4. Architecture

```
NowPlaying.slnx
  src/NowPlaying.Media/          class library: Windows media session wrapper
  src/NowPlaying.Media.Cli/      console harness (nowplaying-cli): probe now, watch in milestone 1
  src/NowPlaying.Plugin/         Stream Deck plugin (StreamDeck-Tools); builds into the sdPlugin folder
    NowPlayingAction.cs          the dial action
    NowPlayingKeyAction.cs       the key action
    FeedbackRenderer.cs          snapshot -> layout items, diffed (pure)
    ArtRenderer.cs               album art + glyph compositing with SkiaSharp (pure)
    MediaHub.cs                  the one shared media service
    PropertyInspectorBridge.cs   the "sessions" data source and the preferred-player global setting
    com.jdlien.now-playing.sdPlugin/
      manifest.json
      layouts/now-playing.json
      pi/dial.html, pi/key.html  property inspectors on sdpi-components v4 (local copy)
      imgs/icons/                play.svg, pause.svg for the layout's pixmap item
      imgs/plugin/, imgs/actions/  placeholder PNG icons for the Stream Deck app
      bin/                       build output, git-ignored; manifest CodePath is bin/NowPlaying.exe
  tools/Check-MediaSessions.ps1  zero-build diagnostic (Windows PowerShell 5.1)
```

### 4.1 Developer loop

```
streamdeck dev                                                   # once: developer mode
streamdeck link src/NowPlaying.Plugin/com.jdlien.now-playing.sdPlugin   # once
streamdeck validate src/NowPlaying.Plugin/com.jdlien.now-playing.sdPlugin

streamdeck stop com.jdlien.now-playing                           # the running plugin locks bin/NowPlaying.exe
dotnet build NowPlaying.slnx
dotnet test NowPlaying.slnx --no-build
streamdeck restart com.jdlien.now-playing

dotnet run --project src/NowPlaying.Media.Cli -- probe           # what Windows exposes right now; --json for machine output
dotnet run --project src/NowPlaying.Media.Cli -- watch --ticks   # live snapshots; type next / prev / toggle / refresh / quit
```

Debug builds of the plugin land straight in the `.sdPlugin/bin/` folder, so
link once, then stop, build, restart. A build while the plugin is running
fails on the locked exe (measured: MSB3021 after ten retries). Release builds
and publishes use the default `bin/` and never touch the linked folder.
StreamDeck-Tools writes `pluginlog.log` next to the exe; the Stream Deck app's
own logs are in `%APPDATA%\Elgato\StreamDeck\logs`.

Runtime components inside the plugin process:

- **MediaSessionService** (singleton). Owns the session manager, chooses the
  active session, subscribes to its events, and publishes an immutable
  `NowPlayingSnapshot` whenever something changes. Exposes `NextAsync`,
  `PreviousAsync`, `TogglePlayPauseAsync`.
- **NowPlayingAction** (one instance per action context). Registered with
  `[PluginActionId("com.jdlien.now-playing.dial")]`. Subscribes to the service
  on `willAppear`, unsubscribes on `willDisappear`, translates dial and touch
  events into service calls, and pushes feedback for its context.
- **FeedbackRenderer**. Pure function from snapshot plus wall clock to the
  `setFeedback` payload. Diffs against the last payload so unchanged fields are
  not resent (every send pushes pixels over USB).
- **ProgressTicker**. A single timer at 1 Hz that runs only while the snapshot
  is `Playing`, has a usable duration, and at least one action is visible.

Data flow: media events, then the service queue, then a snapshot, then each
visible action, then the renderer, then `setFeedback`. Dial input goes from the
action to a service command to the player. The plugin never assumes its own
command succeeded; the display changes only when the media session reports the
new state.

## 5. Media component design

### 5.1 API surface

`Windows.Media.Control` namespace:

- `GlobalSystemMediaTransportControlsSessionManager.RequestAsync()`,
  `GetCurrentSession()`, `GetSessions()`, events `CurrentSessionChanged`,
  `SessionsChanged`.
- Per session: `SourceAppUserModelId`, `TryGetMediaPropertiesAsync()`,
  `GetPlaybackInfo()`, `GetTimelineProperties()`, events
  `MediaPropertiesChanged`, `PlaybackInfoChanged`, `TimelinePropertiesChanged`.
- Commands: `TrySkipNextAsync()`, `TrySkipPreviousAsync()`,
  `TryTogglePlayPauseAsync()`. Each returns a bool that only means the request
  was accepted.
- `PlaybackInfo.Controls` flags (`IsNextEnabled`, `IsPreviousEnabled`,
  `IsPlayPauseToggleEnabled`) say whether a command is currently meaningful.

### 5.2 Snapshot model

```csharp
record NowPlayingSnapshot(
    string? AppId,                  // SourceAppUserModelId, null when no session
    PlaybackState State,            // None, Playing, Paused, Stopped, Changing
    string Title, string Artist,    // already normalised, may be empty
    TimeSpan? Position,             // as reported by the player
    DateTimeOffset? PositionAt,     // LastUpdatedTime for that position
    TimeSpan? Duration,             // null when unknown or not usable
    bool CanNext, bool CanPrevious, bool CanToggle);
```

Artist normalisation: Artist, else AlbumArtist, else AlbumTitle, else empty.
Title: Title, else empty. Missing title and artist together with a live session
shows the app name derived from the app id.

### 5.3 Session selection

Rank every session instead of trusting `GetCurrentSession()`. This is the rule
the `../ak820-pro` agent settled on after a real regression: Windows reported
Apple Music, merely open with nothing loaded, as the current session while
foobar2000 held a paused track, and following "current" showed an empty display.

1. Candidates are sessions whose status is `Playing` or `Paused`, plus
   `Stopped` sessions that still carry a title (foobar2000's Stop button
   leaves the track loaded and reported). `Closed`, `Opened`, and `Changing`
   are not candidates, and neither is a session whose status cannot be read.
2. `Playing` beats `Paused`, which beats `Stopped`.
3. Within the same status, the session that is also the manager's current
   session wins. This keeps a deliberate foreground choice honoured.
4. Remaining ties keep the manager's own order: the first candidate wins.
5. A preferred app id setting (property inspector, later) takes precedence when
   a session with that id is a candidate.
6. Exception for track transitions: the currently chosen session keeps its
   rank while in `Changing` for up to 2 s, so a track change does not flash
   `No media`.

Ranking reads only status and app id, which are cheap synchronous calls.
Metadata is read for the winner alone (see 5.5). Subscribe
`PlaybackInfoChanged` on every session so a status change anywhere triggers a
re-rank; subscribe `MediaPropertiesChanged` and `TimelinePropertiesChanged` on
the winner only. Re-rank also on `CurrentSessionChanged` and `SessionsChanged`.

Switching sessions means unsubscribing the old session's events and subscribing
the new one. Leaked subscriptions are the likely cause of memory growth, so this
is a test item.

### 5.4 Progress and timeline

The timeline is not a clock. `GetTimelineProperties()` returns `Position` and
`LastUpdatedTime`, and each player decides how often to refresh them. Apple
Music refreshes roughly every second. Spotify and some browsers refresh only on
seek, track change, or state change. So:

- Position and duration are relative to `StartTime`: position =
  `Position - StartTime`, duration = `EndTime - StartTime`.
- Displayed position while `Playing` = position + age, where age =
  `now - LastUpdatedTime`, clamped to `[0, duration]`. Trust the age only when
  `0 <= age < 600 s`: a negative age means the clocks disagree, a huge one
  means the timestamp is meaningless. A `LastUpdatedTime` of zero ticks means
  "never updated" (it reads as the year 1601), not "just now". foobar2000
  reports exactly that, so this case is routine rather than theoretical.
- Do the arithmetic on `TimeSpan` and `DateTimeOffset` values, never on seconds
  converted to `double` first. The `../ak820-pro` audit found a 245-second
  track computing as 244.99999999999997 and truncating to 244 that way. .NET
  `TimeSpan` ticks are the same 100 ns unit as WinRT, so subtracting the
  structs is exact.
- Displayed position while `Paused` = `Position`. The bar freezes. Apple Music
  confirms this model: on pause its `LastUpdatedTime` stops advancing.
- On resume, the timeline still carries the report time from the pause, so
  extrapolating by its age would count the whole pause as playback. Seen live
  2026-09-06: a resume showed 4:15 of 4:15 until the next timeline event. The
  service substitutes the resume moment as the report time until the player
  reports a fresh timeline, which for Apple Music takes under a second and for
  a player that never refreshes on resume is what keeps the bar honest.
- Every `TimelinePropertiesChanged` event replaces the base values, which
  corrects drift and handles seeks and track changes.
- The 1 Hz ticker only redraws. It never calls into the media API.
- Duration is usable only when `EndTime > StartTime` and the length is under
  24 hours. Otherwise `Duration` is null and the bar is hidden. This covers
  live streams and players that report garbage end times.
- Bar value = position / duration mapped onto the layout range 0..1000.

### 5.5 Event handling

- `MediaPropertiesChanged` fires several times per track change as fields and
  the thumbnail arrive. Coalesce with a 150 ms debounce, then read properties
  once and publish a snapshot only if it differs from the last one.
- `TryGetMediaPropertiesAsync` can throw or return stale data right after a
  track change. Retry once after 250 ms, then keep the previous metadata.
- Read metadata for the chosen session only, never for every session. The
  `../ak820-pro` audit found that reading properties of an unrelated stopped
  app can stall the whole read while the display keeps a stale position.
- Never poll `TryGetMediaPropertiesAsync` on a timer. The ak820 agents poll at
  3 s because 1 s polling was measured to make Spotify sluggish; this plugin
  reads it only on change events, which is cheaper still.
- Bound every awaited media call with `Task.WaitAsync(TimeSpan)`; 2 s is
  generous. A wedged media broker must produce a logged timeout and a stale
  snapshot, not a hung service. The ak820 Rust worker could not bound its
  waits and isolated them on a thread instead; .NET can bound them.
- Enumerate `GetSessions()` by index and skip entries that fail. The list can
  change underneath the loop when an app closes, and one unreadable session
  must not blind the service to the others.
- Session objects keep their identity across enumerations (verified across a
  30 s re-sync with no churn), so a different object under the same app id
  means the app restarted and its old event subscriptions are dead. The
  service replaces the tracked session and re-subscribes.
- On any read failure keep the previous snapshot and record the failure and
  its time. A broker that blinks should not blank the strip.
- `PlaybackStatus` has six values: Closed, Opened, Changing, Stopped, Playing,
  Paused. `Changing` is transient; keep the last state. `Closed` is treated as
  no session.
- All WinRT events arrive on thread-pool threads. The service funnels them into
  one `Channel<T>` consumed by a single loop, so snapshot publication is
  serialised and no locks are needed.

### 5.6 Commands

- Send exactly one command per user gesture. Check the `Controls` flag first
  and drop the gesture if the command is disabled.
- Commands are awaited only to log the accepted/rejected result. The display
  waits for the session to report the change.
- Previous-track semantics belong to the player. Apple Music and most players
  restart the current track first when a few seconds have elapsed.
- Seeking is out of scope. Apple Music reports seek as not enabled anyway.

### 5.7 Player quirks (known so far)

| Player | Behaviour |
| --- | --- |
| Apple Music | Artist field is `Artist — Album` (em dash) with AlbumTitle empty. Display verbatim in v1; if it needs to be shorter, prefer the part before the em dash. Registers an `Opened` session at launch that Windows calls current. Timeline refreshes about every second while playing and freezes on pause. |
| foobar2000 | v2 registers a session with stock components; no plug-in needed. Title, artist, and thumbnail are correct; AlbumTitle is empty. Timeline is all zeros with a zero `LastUpdatedTime` even while playing, so the bar stays hidden. The control flags report next, previous, and toggle as enabled, so commands should work; confirm in milestone 1. The only known route to a position is its `foo_beefweb` HTTP API, which would be per-app code and is out of scope. |
| Chrome / Edge | App id is plain `Chrome`. Measured 2026-09-06 with YouTube: Title is the video title, Artist is the channel name, and the timeline is complete (position, duration, fresh `LastUpdatedTime`), so the bar and times work. Position depends on the site calling `setPositionState`; YouTube does, other sites may not. |

### 5.8 Borrowed from the ak820-pro keyboard agent

`../ak820-pro` solved the read side of this problem for a keyboard LCD, first
in Python (`hostagent/nowplaying-windows.py`, on the `winsdk` package) and then
in Rust (`ak820-agent/src/smtc/mod.rs` holds the pure ranking and timeline
logic with its tests; `worker.rs` holds the WinRT calls). Reused here:

- The session ranking rule in 5.3 and its regression case.
- The timeline arithmetic in 5.4: start-relative values, tick subtraction, the
  0 to 600 s age gate, zero `LastUpdatedTime` treated as absent.
- The cheap-pass then expensive-pass read order and the by-index enumeration
  in 5.5.
- The measured facts: foobar2000 v2 registers natively without a timeline,
  Apple Music is `Opened` and current at launch, the em dash in Apple Music's
  artist field, and 1 s polling making Spotify sluggish.
- The row assignment: artist on the narrow row beside the icon, title on the
  full-width row. The keyboard LCD had taught the same lesson.
- `ak820 probe` is the same idea as `tools/Check-MediaSessions.ps1`.

Not carried over: the 3 s polling loop (this plugin is event-driven with a
redraw-only ticker), the 30 s keepalive (the Stream Deck app retains feedback,
so a full re-push on `willAppear` is the equivalent), and ASCII folding (the
strip renders Unicode). One deliberate difference: `Stopped` sessions with a
title are low-rank candidates here rather than excluded, because foobar2000's
Stop button leaves a loaded track that a dial press will resume. The Rust `smtc` module is a working, tested
implementation; if one shared component across both projects ever matters more
than staying in C#, it is the one to lift.

## 6. Display design

### 6.1 Layout

Custom layout on the 200 x 100 canvas. Item keys deliberately avoid the reserved
key `title`, which would hand font, colour, and alignment control to the
property inspector.

```json
{
  "$schema": "https://schemas.elgato.com/streamdeck/plugins/layout.json",
  "id": "com.jdlien.now-playing.layout",
  "items": [
    { "key": "icon",     "type": "pixmap", "rect": [2, 2, 36, 36], "zOrder": 1 },
    { "key": "artist",   "type": "text",   "rect": [44, 7, 152, 26], "zOrder": 1,
      "alignment": "left", "font": { "size": 16, "weight": 400 },
      "text-overflow": "ellipsis", "color": "lightGray" },
    { "key": "track",    "type": "text",   "rect": [4, 38, 192, 26], "zOrder": 1,
      "alignment": "left", "font": { "size": 16, "weight": 600 },
      "text-overflow": "ellipsis", "color": "white" },
    { "key": "progress", "type": "bar",    "rect": [8, 68, 184, 8], "zOrder": 1,
      "subtype": 0, "border_w": 0, "range": { "min": 0, "max": 1000 },
      "bar_bg_c": "#333333", "bar_fill_c": "white", "value": 0 },
    { "key": "elapsed",  "type": "text",   "rect": [8, 79, 80, 18], "zOrder": 1,
      "alignment": "left", "font": { "size": 12, "weight": 400 }, "color": "lightGray" },
    { "key": "total",    "type": "text",   "rect": [112, 79, 80, 18], "zOrder": 1,
      "alignment": "right", "font": { "size": 12, "weight": 400 }, "color": "lightGray" }
  ]
}
```

Layouts are not CSS: every item is an absolute rectangle on the 200 x 100
canvas, text has only size, weight, alignment, colour, and overflow, and
there is no padding, margin, or line height. Vertical placement of text
inside its rectangle is the renderer's choice. Tightening the design means
moving rectangles.

Notes from the layout schema:
- `text-overflow` accepts `clip`, `ellipsis`, `fade`; `ellipsis` is the default.
  `fade` is worth a look once real titles are on the strip.
- `font.weight` accepts 100..1000, but how many distinct weights actually
  render depends on the faces the Stream Deck app ships for its UI font.
  Artist and title are both 16 px; the title carries weight 600 against the
  artist's 400. If those two look identical on the strip, try 700.
- `zOrder` is 0..700 and items sharing a zOrder must not overlap.
- `bar.subtype`: 0 rectangle, 1 double rectangle, 2 trapezoid, 3 double
  trapezoid, 4 groove (default).
- `pixmap.value` accepts a file path relative to the plugin folder, a base64
  string, or an SVG string. The play and pause icons ship as SVG files.
- Two lines of text is the practical limit at legible sizes for the title and
  artist. The elapsed and total times sit under the bar at 12 px, as `m:ss`
  or `h:mm:ss` from one hour; the bar moved up 10 px to make room. They are
  blank whenever the bar is hidden.

### 6.1.1 Album art and the key image

The media service exposes the chosen session's thumbnail as an `Artwork`
(bytes plus a content hash) alongside the snapshot, and raises a separate
change event for it, because the thumbnail usually arrives a moment after the
text. Chrome and Apple Music supply PNGs of roughly 20 KB; foobar2000 supplies
one too.

`ArtRenderer` composites with SkiaSharp, the one place the StreamDeck-Tools
dependency earns its place:

- **Dial tile** (36 px, drawn 1:1): the art centre-cropped under rounded
  corners, a uniform dark scrim over the whole tile, and the play/pause glyph
  centred in white at about 60 percent of the tile. It sits in the top-left
  with the artist beside it and the title directly beneath it at full width.
  Sizes tried on the preview: 28 px inside the artist row read as a smudge;
  56 px spanning both rows cost the title a third of its width and lost most
  song titles; 40 px pushed the title too far down. 36 px is where the
  vertical space felt evenly spread: title row at y 38, bar at 68 and 8 px
  tall, times at 79. It is sent as a `data:` URI in the pixmap item, only
  when the art or the state changes.
- **Key image** (144 px): the art full-bleed with a dark translucent circle in
  the bottom-right corner and the glyph inside it. Without art, a large glyph
  on the dark background; with no media, the glyph dimmed.
- **Legibility** comes from the fixed scrim under a white glyph rather than
  from estimating the art's brightness: every cover gets the same treatment,
  and it never flips between tracks. The pixels are ours, so sampling the
  region under the glyph and switching to a dark glyph on light art is a small
  later addition if some covers still fight the scrim.

The key's title is set through the app's own title mechanism, so the user's
font and alignment settings apply and a `showTitle` setting turns it off.

### 6.2 States

| Condition | Icon | Top row (artist) | Full-width row (title) | Bar and times |
| --- | --- | --- | --- | --- |
| No session or Closed | hidden | empty | `No media` | hidden (`enabled: false`), times blank |
| Playing, duration known | play | artist | title | visible, advancing |
| Playing, no duration | play | artist | title | hidden, times blank |
| Paused or Stopped, metadata known | pause | artist | title | frozen at last position |
| Changing | previous | previous | previous | previous |
| Session present, no title or artist | as state | empty | app name | as above |
| Only one of title or artist | as state | artist or empty | title or empty | as above |

### 6.3 Update policy

- Push a full payload on `willAppear` and after any recovery event.
- Otherwise push only changed keys. Metadata and state changes are pushed as
  they arrive (after the debounce). Progress is pushed at 1 Hz while playing
  from StreamDeck-Tools' per-action tick, which reads the service's current
  snapshot and extrapolates. On a 184 px bar and a 4-minute track that is
  roughly one pixel every 1.3 s, so 1 Hz is already smoother than the display
  can show.
- The service raises its change event only for significant changes: state,
  text, duration, a control flag, or a position jump of more than 2 s (a
  seek). Apple Music's twice-a-second timeline updates only refresh the
  current snapshot silently. Measured 2026-09-06: without this, every
  timeline event became a push.
- Nothing is pushed while no action instance is visible.

## 7. Input handling

| Event | Payload fields used | Behaviour |
| --- | --- | --- |
| `dialRotate` | `ticks` (sign), `pressed` | One skip per event in the sign direction, regardless of magnitude. Minimum 200 ms between skips; extra events inside that window are dropped, never queued. Rotation while pressed is ignored in v1 (reserved for seek). |
| `dialDown` | none | Toggle play/pause once. |
| `dialUp` | none | Ignored, so a press never toggles twice. |
| `touchTap` | `hold` | `hold: false` toggles play/pause once. `hold: true` is a distinct gesture and is ignored in v1. |

`ticks` can exceed 1 per event on a fast spin, which is why the policy counts
events, not ticks. One detent still equals one track at normal speed.

Key action:

| Event | Behaviour |
| --- | --- |
| `keyDown` | Record the time. Nothing runs yet. |
| `keyUp` after less than 500 ms | Run the press command (default play/pause). |
| `keyUp` after 500 ms or more | Run the hold command (default next track). The press command does not also run. |

Both actions call `showAlert` when the player rejects a command or there is
no session, which the Marketplace guidelines ask for.

Settings live in property inspectors built on sdpi-components v4, bundled
locally as the guidelines recommend. Per action: `showArt` on both,
`pressAction`, `longPressAction`, and `showTitle` on the key. Global, shared
by every instance: `preferredApp`, whose select is filled by a `sessions`
data source the plugin answers with whatever Windows currently reports. Each
action writes its defaults back into its settings on first appearance so the
inspector shows real values instead of blanks.

Multiple instances: the user may place the action on several dials or pages.
Every instance shares the single MediaSessionService and each visible instance
receives the same snapshot. Input from any instance drives the same session.

## 8. Recovery

| Trigger | Signal | Action |
| --- | --- | --- |
| Player quits or restarts | `SessionsChanged`, `CurrentSessionChanged` | Unsubscribe old session, re-select, publish new snapshot (possibly `No media`). |
| Stream Deck app restarts | Plugin process is restarted | Clean startup; nothing special. |
| USB disconnect and reconnect | `deviceDidDisconnect`, `deviceDidConnect`, then `willAppear` per instance | Re-push the full payload on `willAppear`. |
| Sleep and wake | `systemDidWakeUp` | Re-request the session manager, re-subscribe, re-push. |
| Silent event loss | 30 s timer in the service | Cheap re-sync: re-enumerate sessions, re-rank, re-read the chosen session's status and timeline. Never reads metadata. A stale timeline alone is not treated as a fault, because some players legitimately go a whole track without a timeline event. |
| Stream Deck app exits | WebSocket closes | Dispose the service (unsubscribe, stop ticker) and exit the process. No orphaned exe. |

## 9. Manifest sketch

```json
{
  "$schema": "https://schemas.elgato.com/streamdeck/plugins/manifest.json",
  "UUID": "com.jdlien.now-playing",
  "Name": "Now Playing",
  "Version": "0.1.0.0",
  "Author": "JD Lien",
  "Description": "Shows the current Windows media session on a Stream Deck + dial and controls playback.",
  "Icon": "imgs/plugin/marketplace",
  "Category": "Now Playing",
  "CategoryIcon": "imgs/plugin/category-icon",
  "CodePath": "bin/NowPlaying.exe",
  "SDKVersion": 2,
  "Software": { "MinimumVersion": "7.1" },
  "OS": [ { "Platform": "windows", "MinimumVersion": "10" } ],
  "Actions": [
    {
      "UUID": "com.jdlien.now-playing.dial",
      "Name": "Now Playing",
      "Icon": "imgs/actions/now-playing/icon",
      "Controllers": [ "Encoder" ],
      "Encoder": {
        "layout": "layouts/now-playing.json",
        "TriggerDescription": {
          "Rotate": "Next / previous track",
          "Push": "Play / pause",
          "Touch": "Play / pause"
        }
      },
      "SupportedInMultiActions": false,
      "UserTitleEnabled": false
    }
  ]
}
```

The real file is `src/NowPlaying.Plugin/com.jdlien.now-playing.sdPlugin/manifest.json`;
this sketch shows the shape. `streamdeck validate` requires `CodePath` even for
a Windows-only plugin, so `CodePathWin` is not used. The Stream Deck app
launches that exe with `-port`, `-pluginUUID`, `-registerEvent`, and `-info`
arguments; StreamDeck-Tools consumes them in `SDWrapper.Run(args)`.

## 10. Build plan

Each milestone has an exit test. Do not start the next one until it passes.

1. **Media library and console harness.** Done 2026-09-06. `nowplaying-cli
   watch` prints a line per significant snapshot change, `--ticks` prints the
   extrapolated position once a second, and it takes `next`, `prev`,
   `toggle`, `refresh`, `quit` on stdin. Verified live against Apple Music:
   pause and resume from the harness, position extrapolation, and the resume
   correction. YouTube in Chrome verified on the Stream Deck app's preview.
   Still to run: quitting and relaunching a player while `watch` runs, though
   the plugin log already shows sessions coming and going cleanly.
2. **Plugin skeleton.** Code done; needs the device. The plugin is linked in
   developer mode and its process launches and connects (Stream Deck log:
   "Plugin connected"). Exit still open: drag "Now Playing" from the "Now
   Playing" category onto the rightmost dial of page 1 and confirm the custom
   layout renders. With nothing playing it should show a hidden icon,
   `No media`, and no bar.
3. **Wire display.** Code done (`FeedbackRenderer`, `MediaHub`,
   `NowPlayingAction.OnTick`), unit tested. Exit still open: the section 6.2
   states on the strip, progress advancing once a second, freezing on pause.
4. **Wire input.** Code done. Exit still open: one toggle per press and per
   tap, no toggle on release or on a long touch, one skip per detent, a fast
   spin with no backlog, rotation while pressed ignored.
5. **Recovery.** Code done for wake and device reconnect (full media refresh
   and full re-push). Exit still open: the section 8 rows on the device.
6. **Measure and package.** Not started. Section 11 measurements, then
   `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`
   into the sdPlugin `bin/`, then `streamdeck pack`. Exit: installs from the
   `.streamDeckPlugin` on a fresh profile and works.

When a check fails, `bin/pluginlog.log` in the sdPlugin folder has the
plugin's own log (media events are tagged `[media]`, action events
`[action]`), and `%APPDATA%\Elgato\StreamDeck\logs\StreamDeck.log` has the
app's side.

## 11. Validation

Functional (run against Apple Music first, then Chrome with YouTube, then
foobar2000, which needs no plug-in and should show text and icon with the bar
hidden):

- Title and artist update on track change, including tracks that share a title.
- External play, pause, and stop from the player's own UI update the icon.
- Next and previous from the dial; previous-restarts-track behaviour noted.
- Exactly one toggle per dial press and per touch tap; long touch does nothing.
- Long titles and artists truncate with ellipsis; CJK, accented, and emoji text
  render; empty artist and empty title cases.
- Live stream or missing duration hides the bar; seeking snaps the bar.
- Two players: music playing while a browser tab opens, then starts playing,
  then closes.
- Fast dial spin in both directions: no skip backlog.
- Recovery table in section 8, row by row, including a real sleep/wake cycle.
- Placing the action on two dials at once.

Resource measurements (Process Explorer or `Get-Process` sampled every minute
for at least an hour):

- CPU near 0% idle with no session, near 0% with a paused session, under 1%
  while playing.
- Private bytes flat over an hour of playback with track changes every minute.
- Timer stops when the action leaves the screen; verify with CPU at 0%.
- Plugin process exits within a few seconds of quitting the Stream Deck app.

Diagnostic script: `tools/Check-MediaSessions.ps1` lists every session with
metadata, control flags, and timeline samples. Run it in Windows PowerShell 5.1
(`powershell.exe`), not PowerShell 7, because WinRT projection support is built
into 5.1.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Check-MediaSessions.ps1 -Samples 3 -IntervalSeconds 3
```

## 12. Open questions

1. Apple Music's `Artist — Album` field: show verbatim (v1 default) or split
   at the em dash and drop the album?
2. foobar2000 progress: accept no bar (v1 default), or add per-app position
   reads through its `foo_beefweb` HTTP API later?
3. Later candidates, not planned: long-touch or press-and-rotate mapped to
   seek, a luminance-aware glyph colour on the art, a proper plugin icon and
   Marketplace listing assets (section 13).

## 13. Marketplace

Checked 2026-09-06 against Elgato's plugin guidelines and Maker Console docs:

- Submission is a Maker account, an English listing (name, description,
  256 and 512 px plugin icon, thumbnail, gallery images, links), the
  `.streamDeckPlugin` from `streamdeck pack`, and a 4 to 10 working day review
  by email. Plugins needing hardware also need a short video. Free is fine;
  name and monetization cannot change afterwards. No code signing requirement
  appears in the docs.
- Guideline items this plugin now meets: two actions (they ask for 2 to 30),
  configurable actions with a property inspector, monochrome white action and
  category icons, `showAlert` on failure, layout updates well under 10 per
  second, a UUID with author and plugin name that must never change.
- Still needed: a real plugin icon (the placeholder triangle does not
  "accurately portray" anything), gallery screenshots, listing copy, and a
  name check against the existing "Current Media (Now Playing)" plugin;
  something like "Now Playing Dial" avoids the collision.

## References

- [Elgato: Dials and Touch Strip guide](https://docs.elgato.com/streamdeck/sdk/guides/dials/)
- [Elgato: layout JSON schema](https://schemas.elgato.com/streamdeck/plugins/layout.json)
- [Elgato: manifest reference](https://docs.elgato.com/streamdeck/sdk/references/manifest/)
- [Elgato: plugin WebSocket reference](https://docs.elgato.com/streamdeck/sdk/references/websocket/plugin/)
- [Elgato: Stream Deck CLI](https://docs.elgato.com/streamdeck/cli/intro)
- [StreamDeck-Tools on NuGet](https://www.nuget.org/packages/StreamDeck-Tools/) and [on GitHub](https://github.com/BarRaider/streamdeck-tools)
- [Microsoft: GlobalSystemMediaTransportControlsSession](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession?view=winrt-26100)
- Sibling project `../ak820-pro`: `ak820-agent/src/smtc/` (Rust ranking and timeline logic with tests), `hostagent/nowplaying-windows.py` (Python original), `docs/hardware.md` (player behaviour measured 2026-09-05)
