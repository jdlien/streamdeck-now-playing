#!/bin/bash
# Package the macOS plugin: publish, sign, notarize, staple.
#
# Two findings from the S4 spike are baked in here, both of which cost an
# afternoon to establish (macos-port-plan M5):
#
#  1. Self-contained is not optional. A framework-dependent publish launches
#     from a terminal but not when the Stream Deck app spawns it, because the
#     app's environment does not lead the apphost to a shared runtime.
#
#  2. Any quarantine attribute on the payload stops the plugin dead. macOS
#     blocks the unnotarized binary and the only symptom is the app reporting
#     "Process stopped (terminated)" and eventually disabling the plugin. So
#     attributes are stripped after publishing, and notarization is what makes
#     it work on someone else's machine.
#
# Usage:
#   ./build/package-macos.sh                # publish + sign only
#   ./build/package-macos.sh --notarize     # also notarize and staple
#
# Notarization needs a stored credential profile. Set it up once with:
#   ./build/notarize-setup.sh '<secrt-url>'      # fetches the app-specific password
#   ./build/notarize-setup.sh --check            # confirm it is stored
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
PLUGIN="$REPO/src/NowPlaying.Plugin/com.jdlien.now-playing.sdPlugin"
STAGE="$REPO/build/stage/com.jdlien.now-playing.sdPlugin"
IDENTITY="${CODESIGN_IDENTITY:-Developer ID Application: Joseph Lien (A93Q7MKECL)}"
PROFILE="${NOTARY_PROFILE:-NowPlayingNotary}"
NOTARIZE=0
[ "${1:-}" = "--notarize" ] && NOTARIZE=1

echo "==> staging"
rm -rf "$REPO/build/stage"
mkdir -p "$STAGE"
# Everything except build output and logs; bin is republished below.
rsync -a --exclude 'bin/' --exclude '*.log' "$PLUGIN/" "$STAGE/"

echo "==> publishing macOS binary (self-contained, osx-arm64)"
dotnet publish "$REPO/src/NowPlaying.Plugin/NowPlaying.Plugin.csproj" \
  -c Release -f net10.0 -r osx-arm64 --self-contained true \
  -o "$STAGE/bin/mac" --nologo -v q

echo "==> stripping extended attributes"
# A stray com.apple.quarantine here is fatal and silent. See note 2 above.
xattr -cr "$STAGE"

echo "==> signing with: $IDENTITY"
# Inside out: every nested binary first, then the executable that loads them.
find "$STAGE/bin/mac" -type f \( -name '*.dylib' -o -name '*.so' \) -print0 \
  | xargs -0 -n1 codesign --force --timestamp --options runtime --sign "$IDENTITY"
codesign --force --timestamp --options runtime \
  --entitlements "$REPO/build/entitlements.plist" --sign "$IDENTITY" "$STAGE/bin/mac/NowPlaying"
codesign --verify --strict --verbose=1 "$STAGE/bin/mac/NowPlaying"

if [ "$NOTARIZE" = "1" ]; then
  echo "==> notarizing (submitting a zip of the payload)"
  ZIP="$REPO/build/notarize.zip"
  rm -f "$ZIP"
  ditto -c -k --keepParent "$STAGE/bin/mac" "$ZIP"
  if ! xcrun notarytool history --keychain-profile "$PROFILE" >/dev/null 2>&1; then
    echo "    no credential profile '$PROFILE'. Run: ./build/notarize-setup.sh '<secrt-url>'" >&2
    exit 1
  fi
  xcrun notarytool submit "$ZIP" --keychain-profile "$PROFILE" --wait
  # Stapling attaches the ticket so Gatekeeper can verify without a network
  # round trip. A bare executable cannot be stapled -- only bundles, disk images
  # and installer packages can -- so this is attempted on the packed plugin and
  # is not fatal if it is refused. Notarization still counts: Gatekeeper checks
  # online when there is no stapled ticket.
  echo "==> notarization accepted"
fi

echo "==> packing"
if command -v streamdeck >/dev/null 2>&1; then
  streamdeck pack "$STAGE" --output "$REPO/build" --force
  PACKED="$REPO/build/com.jdlien.now-playing.streamDeckPlugin"
  if [ "$NOTARIZE" = "1" ] && [ -f "$PACKED" ]; then
    if xcrun stapler staple "$PACKED" 2>/dev/null; then
      echo "    ticket stapled"
    else
      echo "    not staplable (expected for this container format); Gatekeeper will check online"
    fi
  fi
else
  echo "    streamdeck CLI not installed; install with: npm i -g @elgato/cli"
  echo "    staged payload is ready at: $STAGE"
fi

echo "==> done"
du -sh "$STAGE" | awk '{print "    payload: " $1}'
