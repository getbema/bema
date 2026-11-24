#!/bin/bash
# Build Bema for other people: a universal Release build, signed with Developer ID and the hardened
# runtime, notarised by Apple, stapled, in a .dmg; the update signed with Sparkle and added to
# site/appcast.xml.
#
#   Scripts/release.sh             # build, sign, notarise -> dist/Bema-<version>.dmg and dist/Bema.dmg
#   Scripts/release.sh --local     # sign with any identity (IDENTITY=...), skip notarising: to test signing
#   Scripts/release.sh --publish   # the same, then create GitHub release v<version> with Bema.dmg
#                                  # (notes from docs/release-notes/v<version>.md) and deploy getbema.dev
#   NOTARY_ID=<id> Scripts/release.sh --publish
#                                  # resume after notarisation gave up: reuse dist/Bema-<version>.dmg and
#                                  # wait for that submission (xcrun notarytool history lists the ids)
#
# Needs: a "Developer ID Application" certificate in the keychain; notarytool credentials stored under
# a keychain profile (xcrun notarytool store-credentials <name> ...), named in NOTARY_PROFILE or in a
# local, untracked .release.env (NOTARY_PROFILE=<name>);
# Sparkle's private key for the account "bema" in the login keychain (made once with
# .build/artifacts/sparkle/Sparkle/bin/generate_keys --account bema; its public key is SUPublicEDKey in
# Resources/Info.plist). The version is CFBundleShortVersionString / CFBundleVersion in Resources/Info.plist.
set -euo pipefail

# Tells Launch Services to forget a copy of Bema, so "open Bema" only ever finds the installed one.
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
forget_app() { "$LSREGISTER" -u "$1" 2>/dev/null || true; }

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
# Local settings that never go in the repository (the notarytool profile name).
[[ -f "$ROOT/.release.env" ]] && source "$ROOT/.release.env"
MODE="${1:-}"
IDENTITY="${IDENTITY:-Developer ID Application}"
PROFILE="${NOTARY_PROFILE:-}"
[[ -n "$PROFILE" || "${1:-}" == "--local" ]] || { echo "Set NOTARY_PROFILE (or put NOTARY_PROFILE=<name> in .release.env)." >&2; exit 1; }
REPO="${REPO:-getbema/bema}"
OUT="$ROOT/dist"
PLIST="$ROOT/Resources/Info.plist"
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$PLIST")
BUILDNO=$(/usr/libexec/PlistBuddy -c "Print CFBundleVersion" "$PLIST")
NAME="Bema-$VERSION"
SPARKLE_BIN="$ROOT/.build/artifacts/sparkle/Sparkle/bin"

log() { printf '\n==> %s\n' "$*"; }
jsonget() { /usr/bin/python3 -I -c 'import json,sys
try: print(json.load(open(sys.argv[1])).get(sys.argv[2],""))
except Exception: print("")' "$1" "$2"; }

if ! security find-identity -v -p codesigning | grep -q "$IDENTITY"; then
  echo "No signing identity matching \"$IDENTITY\" in the keychain." >&2
  exit 1
fi
# Sign by hash: the keychain can hold two copies of the same certificate, which codesign calls ambiguous.
HASH=$(security find-identity -v -p codesigning | grep "$IDENTITY" | head -1 | awk '{print $2}')

if [[ "$MODE" == "--publish" ]]; then
  [[ -z "$(git status --porcelain)" ]] || { echo "Commit or stash your changes first: a release is built from what's committed." >&2; exit 1; }
  [[ -f "$ROOT/docs/release-notes/v$VERSION.md" ]] || { echo "Write docs/release-notes/v$VERSION.md first." >&2; exit 1; }
  gh release view "v$VERSION" --repo "$REPO" > /dev/null 2>&1 && { echo "v$VERSION is already released. Bump the version in Resources/Info.plist." >&2; exit 1; }
fi

if [[ -n "${NOTARY_ID:-}" ]]; then
  # Resume: the DMG was built and uploaded by an earlier run whose wait gave up; carry on from it.
  [[ -f "$OUT/$NAME.dmg" ]] || { echo "NOTARY_ID is set but $OUT/$NAME.dmg isn't there." >&2; exit 1; }
  ID="$NOTARY_ID"
  log "Resuming with $OUT/$NAME.dmg and submission $ID"
else
log "Testing"
swift test > "$ROOT/build/release-tests.log" 2>&1 || { tail -30 "$ROOT/build/release-tests.log"; echo "Tests failed." >&2; exit 1; }

log "Building Bema $VERSION ($BUILDNO), universal"
swift build -c release --arch arm64 --arch x86_64 > "$ROOT/build/release-build.log" 2>&1 \
  || { grep -E "error:" "$ROOT/build/release-build.log" | head; exit 1; }
BINDIR="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)"

APP="$ROOT/build/release/Bema.app"
rm -rf "$ROOT/build/release"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks"
cp "$BINDIR/Bema" "$APP/Contents/MacOS/Bema"
cp "$PLIST" "$APP/Contents/Info.plist"
cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/"
cp "$ROOT/site/office/manifest.xml" "$APP/Contents/Resources/PowerPointAddin.xml"
ditto "$BINDIR/Sparkle.framework" "$APP/Contents/Frameworks/Sparkle.framework"
# Only the app's own Frameworks folder and the system's Swift libraries: drop the build machine's toolchain path.
for rp in $(otool -l "$APP/Contents/MacOS/Bema" | awk '/LC_RPATH/{getline; getline; print $2}' | grep -v -e '^@executable_path' -e '^/usr/lib/swift$' | sort -u); do
  install_name_tool -delete_rpath "$rp" "$APP/Contents/MacOS/Bema" 2>/dev/null || true
done
LOADS=$(otool -l "$APP/Contents/MacOS/Bema")   # captured first: grep -q closing the pipe early would fail under pipefail
[[ "$LOADS" == *"@executable_path/../Frameworks"* ]] || { echo "The app can't find Sparkle (no Frameworks rpath)." >&2; exit 1; }
lipo -info "$APP/Contents/MacOS/Bema"

log "Signing with Developer ID and the hardened runtime"
SPARKLE="$APP/Contents/Frameworks/Sparkle.framework"
# Sparkle's helpers, inside out, as Sparkle documents for Developer ID (the downloader keeps its entitlements).
codesign --force --timestamp --options runtime --sign "$HASH" "$SPARKLE/Versions/B/XPCServices/Installer.xpc"
codesign --force --timestamp --options runtime --sign "$HASH" --preserve-metadata=entitlements "$SPARKLE/Versions/B/XPCServices/Downloader.xpc"
codesign --force --timestamp --options runtime --sign "$HASH" "$SPARKLE/Versions/B/Autoupdate"
codesign --force --timestamp --options runtime --sign "$HASH" "$SPARKLE/Versions/B/Updater.app"
codesign --force --timestamp --options runtime --sign "$HASH" "$SPARKLE"
codesign --force --timestamp --options runtime --entitlements "$ROOT/Scripts/bema.entitlements" --sign "$HASH" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

log "Making the disk image"
mkdir -p "$OUT"
STAGE=$(mktemp -d)
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$OUT/$NAME.dmg"
hdiutil create -volname "Bema" -srcfolder "$STAGE" -ov -format UDZO "$OUT/$NAME.dmg" > /dev/null
rm -rf "$STAGE"
codesign --force --timestamp --sign "$HASH" "$OUT/$NAME.dmg"
# The disk image has the app now. Remove the build copy so opening Bema never launches it from the
# repository instead of /Applications (two copies running made Sparkle's installer fail to launch).
forget_app "$APP"
rm -rf "$ROOT/build/release"

if [[ "$MODE" == "--local" ]]; then
  log "Local build (not notarised): $OUT/$NAME.dmg"
  exit 0
fi

log "Sending to Apple to be notarised (usually a few minutes)"
# Upload, then poll the submission. A single long `--wait` dies when the connection drops while
# Apple is slow, though the submission carries on; polling survives that.
ID=""
for attempt in 1 2 3; do
  xcrun notarytool submit "$OUT/$NAME.dmg" --keychain-profile "$PROFILE" --output-format json > "$OUT/$NAME.notary.json" || true
  ID=$(jsonget "$OUT/$NAME.notary.json" id)
  [[ -n "$ID" ]] && break
  echo "Upload failed (try $attempt of 3); trying again in 30 seconds." >&2
  sleep 30
done
[[ -n "$ID" ]] || { echo "Notarisation: the upload didn't reach Apple." >&2; exit 1; }
fi
echo "Submission $ID; checking every 30 seconds for up to two hours."
STATUS=""
for _ in $(seq 1 240); do
  xcrun notarytool info "$ID" --keychain-profile "$PROFILE" --output-format json > "$OUT/$NAME.notary.json" 2>/dev/null || true
  STATUS=$(jsonget "$OUT/$NAME.notary.json" status)
  [[ "$STATUS" == "Accepted" || "$STATUS" == "Invalid" || "$STATUS" == "Rejected" ]] && break
  sleep 30
done
if [[ "$STATUS" != "Accepted" ]]; then
  echo "Notarisation: ${STATUS:-no answer} (submission $ID). Apple's log:" >&2
  xcrun notarytool log "$ID" --keychain-profile "$PROFILE" >&2 || true
  exit 1
fi

log "Stapling the ticket"
xcrun stapler staple "$OUT/$NAME.dmg"
xcrun stapler validate "$OUT/$NAME.dmg"

log "Checking Gatekeeper accepts it"
spctl --assess --type open --context context:primary-signature --verbose=2 "$OUT/$NAME.dmg"
MOUNT=$(mktemp -d)
hdiutil attach -nobrowse -readonly -mountpoint "$MOUNT" "$OUT/$NAME.dmg" > /dev/null
spctl --assess --type execute --verbose=2 "$MOUNT/Bema.app"
forget_app "$MOUNT/Bema.app"
hdiutil detach "$MOUNT" > /dev/null

# The same file under the stable name the site and the appcast link to.
cp "$OUT/$NAME.dmg" "$OUT/Bema.dmg"

log "Signing the update and adding it to site/appcast.xml"
SIGNATURE=$("$SPARKLE_BIN/sign_update" --account bema "$OUT/Bema.dmg")
NOTES_HTML=""
[[ -f "$ROOT/docs/release-notes/v$VERSION.md" ]] && NOTES_HTML="<p>See the release notes on GitHub: https://github.com/$REPO/releases/tag/v$VERSION</p>"
/usr/bin/python3 -I "$ROOT/Scripts/appcast.py" "$ROOT/site/appcast.xml" "$VERSION" "$BUILDNO" \
  "https://github.com/$REPO/releases/download/v$VERSION/Bema.dmg" "$SIGNATURE" "$NOTES_HTML"

if [[ "$MODE" == "--publish" ]]; then
  log "Publishing v$VERSION on GitHub"
  gh release create "v$VERSION" "$OUT/Bema.dmg" --repo "$REPO" --title "Bema $VERSION" \
    --notes-file "$ROOT/docs/release-notes/v$VERSION.md" --target main
  log "Committing the appcast and deploying getbema.dev"
  git add site/appcast.xml
  git commit -q -m "Appcast: Bema $VERSION"
  git push -q
  (cd "$ROOT/site" && npx --yes wrangler deploy > /dev/null)
  curl -fsS https://getbema.dev/appcast.xml | grep -q "<sparkle:shortVersionString>$VERSION<" \
    && echo "getbema.dev/appcast.xml offers $VERSION." || echo "Check getbema.dev/appcast.xml: $VERSION isn't there yet." >&2
fi

log "Done: $OUT/$NAME.dmg"
