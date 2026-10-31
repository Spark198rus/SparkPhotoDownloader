#!/usr/bin/env bash
# Build Spark Photo Downloader as a macOS .app bundle.
#
# Runs on macOS or on Windows/Linux. On macOS it can also merge architectures, sign,
# notarise and produce a .dmg; elsewhere it cross-compiles the bundle and packs it into a
# .tar.gz that preserves the executable bit, ready to copy over.
#
#   ./build/macos/build-macos.sh                          # arm64 (Apple silicon)
#   ./build/macos/build-macos.sh --arch x64               # Intel
#   ./build/macos/build-macos.sh --arch universal         # both (macOS host only)
#   ./build/macos/build-macos.sh --sign "Developer ID Application: Name (TEAMID)"
#   ./build/macos/build-macos.sh --sign "..." --notarize NOTARY_PROFILE
#
# Without --sign the bundle is still signed ad hoc: Apple silicon refuses to run arm64 code
# that carries no signature at all, and Gatekeeper reports such an app as "damaged". On macOS
# that uses codesign; elsewhere it needs rcodesign (https://github.com/indygreg/apple-platform-rs,
# `cargo install apple-codesign`, or a prebuilt binary on PATH). --unsigned skips it.

set -euo pipefail

ARCH="arm64"
SIGN_IDENTITY=""
NOTARY_PROFILE=""
SKIP_ADHOC=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch)     ARCH="$2"; shift 2 ;;
        --sign)     SIGN_IDENTITY="$2"; shift 2 ;;
        --notarize) NOTARY_PROFILE="$2"; shift 2 ;;
        --unsigned) SKIP_ADHOC=true; shift ;;
        -h|--help)  sed -n '2,19p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

ON_MAC=false
[[ "$(uname -s)" == "Darwin" ]] && ON_MAC=true

if [[ "$ARCH" == "universal" && "$ON_MAC" != true ]]; then
    echo "universal builds need lipo, which only ships with macOS." >&2
    echo "Build --arch arm64 and --arch x64 separately, or run this on the Mac." >&2
    exit 1
fi

if [[ -n "$SIGN_IDENTITY$NOTARY_PROFILE" && "$ON_MAC" != true ]]; then
    echo "signing and notarising need Apple's codesign and notarytool, so they only run on macOS." >&2
    exit 1
fi

if [[ -n "$NOTARY_PROFILE" && -z "$SIGN_IDENTITY" ]]; then
    echo "--notarize needs --sign: Apple only notarises Developer ID signed apps." >&2
    exit 1
fi

# Fail before the slow publish rather than after it: an unsigned arm64 bundle is not a
# usable artifact, it is the "app is damaged" error waiting to happen on the user's Mac.
if [[ -z "$SIGN_IDENTITY" && "$SKIP_ADHOC" != true && "$ON_MAC" != true ]] \
        && ! command -v rcodesign >/dev/null 2>&1; then
    echo "rcodesign was not found on PATH." >&2
    echo "Apple silicon will not run an unsigned bundle, so cross-compiled builds are signed" >&2
    echo "ad hoc with rcodesign. Install it with 'cargo install apple-codesign', or download a" >&2
    echo "release from https://github.com/indygreg/apple-platform-rs/releases." >&2
    echo "Pass --unsigned to build anyway (the result will not launch on an M-series Mac)." >&2
    exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/SparkPhotoDownloader2.csproj"
APP_NAME="Spark Photo Downloader"
OUT="$ROOT/artifacts/macos"
APP="$OUT/$APP_NAME.app"

publish_one() {
    local rid="$1" dest="$2"
    echo "==> publishing $rid"
    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:DebugType=none \
        -o "$dest"
}

# Clear only what this run rewrites, so building arm64 and then x64 leaves both archives in
# place rather than the second wiping the first.
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

case "$ARCH" in
    arm64|x64)
        publish_one "osx-$ARCH" "$APP/Contents/MacOS"
        ;;
    universal)
        # Publish both slices, then merge every Mach-O file so one bundle runs natively on
        # Apple silicon and on Intel.
        publish_one "osx-arm64" "$OUT/stage-arm64"
        publish_one "osx-x64"   "$OUT/stage-x64"
        cp -R "$OUT/stage-arm64/." "$APP/Contents/MacOS/"

        while IFS= read -r -d '' arm_file; do
            rel="${arm_file#"$OUT/stage-arm64/"}"
            x64_file="$OUT/stage-x64/$rel"
            [[ -f "$x64_file" ]] || continue

            if file "$arm_file" | grep -q 'Mach-O'; then
                lipo -create "$arm_file" "$x64_file" -output "$APP/Contents/MacOS/$rel" 2>/dev/null \
                    || cp "$arm_file" "$APP/Contents/MacOS/$rel"
            fi
        done < <(find "$OUT/stage-arm64" -type f -print0)

        rm -rf "$OUT/stage-arm64" "$OUT/stage-x64"
        ;;
    *)
        echo "unknown arch: $ARCH (expected arm64, x64 or universal)" >&2
        exit 1
        ;;
esac

cp "$ROOT/build/macos/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/Assets/app.icns" "$APP/Contents/Resources/app.icns"
chmod +x "$APP/Contents/MacOS/SparkPhotoDownloader" 2>/dev/null || true

echo "==> bundle assembled: $APP"

# ---- signing and notarisation -----------------------------------------------------------

if [[ -n "$SIGN_IDENTITY" ]]; then
    # Finder metadata and quarantine flags make codesign refuse the bundle.
    xattr -cr "$APP"
    echo "==> signing"
    ENTITLEMENTS="$ROOT/build/macos/entitlements.plist"

    # Inner binaries first, then the bundle. The hardened runtime is required for
    # notarisation, and the JIT entitlements are required because .NET compiles at run time.
    find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -perm -u+x \) -print0 |
        while IFS= read -r -d '' binary; do
            codesign --force --timestamp --options runtime \
                --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY" "$binary" >/dev/null
        done

    codesign --force --timestamp --options runtime \
        --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY" "$APP"
    codesign --verify --deep --strict --verbose=2 "$APP"
    echo "==> signed"
elif [[ "$SKIP_ADHOC" != true ]]; then
    # Ad hoc signature ("-"): no certificate, no Gatekeeper approval, but a valid code
    # signature over every Mach-O file and the bundle as a whole. That is the minimum Apple
    # silicon demands before it will execute anything; without it the kernel kills the
    # process and Finder calls the app damaged. No hardened runtime, so no JIT entitlements.
    echo "==> signing ad hoc"
    if [[ "$ON_MAC" == true ]]; then
        xattr -cr "$APP"
        codesign --force --deep --sign - "$APP"
        codesign --verify --deep --strict --verbose=2 "$APP"
    else
        # rcodesign signs ad hoc when given no key, walking the bundle and sealing every
        # nested Mach-O before the main executable.
        rcodesign sign "$APP"
    fi
    echo "==> signed ad hoc"
else
    echo "==> WARNING: bundle left unsigned (--unsigned); it will not launch on Apple silicon" >&2
fi

if [[ -n "$NOTARY_PROFILE" ]]; then
    echo "==> notarising"
    ZIP="$OUT/$APP_NAME.zip"
    ditto -c -k --keepParent "$APP" "$ZIP"
    xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$APP"
    rm -f "$ZIP"
    echo "==> notarised and stapled"
fi

# ---- packaging ---------------------------------------------------------------------------

if [[ "$ON_MAC" == true ]]; then
    DMG="$OUT/$APP_NAME.dmg"
    rm -f "$DMG"
    hdiutil create -volname "$APP_NAME" -srcfolder "$APP" -ov -format UDZO "$DMG" >/dev/null
    echo "==> disk image: $DMG"
    du -sh "$APP" "$DMG"
else
    # A Windows or Linux file system cannot mark the binary executable in a way macOS will
    # honour, and a .zip made here would arrive without the bit set. tar can record the mode
    # explicitly, so the bundle is usable the moment it is unpacked on the Mac.
    TARBALL="$OUT/SparkPhotoDownloader-macos-$ARCH.tar.gz"
    ( cd "$OUT" && tar czf "$TARBALL" --mode='0755' "$APP_NAME.app" )

    echo "==> archive: $TARBALL"
    echo
    echo "Copy it to the Mac, then:"
    echo "    tar xzf \"$(basename "$TARBALL")\""
    echo "    xattr -cr \"$APP_NAME.app\"    # required: otherwise macOS calls it damaged"
    echo "    open \"$APP_NAME.app\""
fi
