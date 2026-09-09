#!/bin/bash
# One-time notarization credential setup.
#
# Fetches an app-specific password from a secrt.ca one-time link and stores it
# in the login Keychain as a notarytool profile. After this, packaging can
# notarize on its own:
#
#   ./build/package-macos.sh --notarize
#
# Usage:
#   ./build/notarize-setup.sh 'https://secrt.ca/s/abc#key'
#   ./build/notarize-setup.sh --check          # is a profile already stored?
#
# The app-specific password comes from appleid.apple.com -> Sign-In and
# Security -> App-Specific Passwords. It is NOT the Apple ID password.
#
# Two deliberate choices about handling the secret:
#
#   * The password is never passed as a command-line argument, because argv is
#     readable by any process on the machine via ps. notarytool offers a secure
#     prompt when --password is omitted, and expect(1) drives that prompt.
#   * The password is never written to disk, never echoed, and never logged.
#     It lives in one shell variable and in the Keychain.
#
# secrt links are one-time: retrieving burns the secret. If this script fails
# after the fetch, the link is already spent and a fresh one is needed.
set -euo pipefail

APPLE_ID="${NOTARY_APPLE_ID:-jd@jdlien.com}"
TEAM_ID="${NOTARY_TEAM_ID:-A93Q7MKECL}"
PROFILE="${NOTARY_PROFILE:-NowPlayingNotary}"

have_profile() {
    # A stored profile makes history work without any credential prompt.
    xcrun notarytool history --keychain-profile "$PROFILE" >/dev/null 2>&1
}

if [ "${1:-}" = "--check" ]; then
    if have_profile; then
        echo "Profile '$PROFILE' is stored and working."
        exit 0
    fi
    echo "No working profile named '$PROFILE'. Run:"
    echo "  ./build/notarize-setup.sh '<secrt-url>'"
    exit 1
fi

URL="${1:-}"
if [ -z "$URL" ]; then
    echo "usage: $0 '<secrt-url>'   (or --check)" >&2
    exit 2
fi

if have_profile; then
    echo "Profile '$PROFILE' already works; not spending the secrt link."
    echo "Delete it first if you need to replace it:"
    echo "  security delete-generic-password -l 'com.apple.gke.notary.tool.saved-creds' -a '$PROFILE'"
    exit 0
fi

command -v secrt >/dev/null || { echo "secrt CLI not found on PATH" >&2; exit 3; }

echo "==> retrieving the app-specific password (this burns the one-time link)"
PASSWORD="$(secrt get "$URL" -o - --silent)" || { echo "secrt retrieval failed" >&2; exit 4; }
# Trim any trailing newline the transport added; Apple rejects a password with one.
PASSWORD="${PASSWORD%%$'\n'*}"
if [ -z "$PASSWORD" ]; then
    echo "secrt returned an empty secret" >&2
    exit 5
fi
SAVED="$PASSWORD"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
echo "    got ${#PASSWORD} characters"

echo "==> storing Keychain profile '$PROFILE' for $APPLE_ID (team $TEAM_ID)"
# expect feeds notarytool's secure prompt so the password stays out of argv.
# log_user 0 keeps the exchange, including the password, off the terminal.
STATUS=0
PASSWORD="$PASSWORD" PROFILE_NAME="$PROFILE" APPLE_ID_ARG="$APPLE_ID" TEAM_ID_ARG="$TEAM_ID" \
expect <<'EXPECT' || STATUS=$?
    log_user 0
    set timeout 180
    set pw $env(PASSWORD)
    spawn xcrun notarytool store-credentials $env(PROFILE_NAME) \
        --apple-id $env(APPLE_ID_ARG) --team-id $env(TEAM_ID_ARG)
    # Answer the prompt, then let the process finish and report its own exit
    # code. Matching on "success" or "error" text would be guessing at wording
    # that Apple can change; the exit status is the fact.
    expect {
        -re {(?i)password[^\r\n]*:} { send -- "$pw\r"; exp_continue }
        timeout                       { exit 2 }
        eof                           { }
    }
    catch wait result
    exit [lindex $result 3]
EXPECT
unset PASSWORD

if [ "$STATUS" != "0" ]; then
    # The link is already spent, so keep the secret rather than lose it. This
    # file is gitignored and owner-read-only; delete it once the Keychain
    # profile is in place.
    mkdir -p "$REPO_ROOT/.secrets"
    ( umask 077; printf '%s' "$SAVED" > "$REPO_ROOT/.secrets/notary-password" )
    unset SAVED
    echo "storing credentials failed (exit $STATUS)." >&2
    echo "The password was saved to .secrets/notary-password (gitignored, 0600)" >&2
    echo "so the one-time link is not wasted. Retry with:" >&2
    echo "  xcrun notarytool store-credentials $PROFILE --apple-id $APPLE_ID --team-id $TEAM_ID" >&2
    exit "$STATUS"
fi
unset SAVED

echo "==> verifying"
if have_profile; then
    echo "    profile '$PROFILE' stored and validated against Apple."
    echo
    echo "Now run:  ./build/package-macos.sh --notarize"
else
    echo "    stored, but a test call failed. Check the Apple ID and team id." >&2
    exit 6
fi
