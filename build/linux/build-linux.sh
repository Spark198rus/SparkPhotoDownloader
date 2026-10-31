#!/usr/bin/env bash
# Build Spark Photo Downloader for Linux.
#
# Runs on Linux, or cross-compiles from Windows or macOS. Always produces a portable
# .tar.gz with an installer; also produces a .deb, and an AppImage when appimagetool is
# on PATH.
#
#   ./build/linux/build-linux.sh                  # x64
#   ./build/linux/build-linux.sh --arch arm64     # aarch64
#   ./build/linux/build-linux.sh --arch both
#   ./build/linux/build-linux.sh --no-deb

set -euo pipefail

ARCH="x64"
MAKE_DEB=true

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch)   ARCH="$2"; shift 2 ;;
        --no-deb) MAKE_DEB=false; shift ;;
        -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/SparkPhotoDownloader2.csproj"
ASSETS="$ROOT/Assets"
OUT="$ROOT/artifacts/linux"

APP_ID="spark-photo-downloader"
APP_NAME="Spark Photo Downloader"
VERSION="1.0.1"

# ---------------------------------------------------------------------------------------
# A .deb is an ar archive of exactly three members, in order. dpkg-deb and ar are not
# available everywhere this script needs to run, so the container is written by hand: the
# format is a 8-byte magic followed by 60-byte ASCII headers.
# ---------------------------------------------------------------------------------------
ar_append() {
    local archive="$1" member="$2" file="$3"
    local size
    size=$(wc -c < "$file")

    printf '%-16s%-12u%-6u%-6u%-8s%-10u\140\n' \
        "$member" "$(date +%s)" 0 0 "100644" "$size" >> "$archive"
    cat "$file" >> "$archive"

    # Members are padded to an even length.
    (( size % 2 == 1 )) && printf '\n' >> "$archive"
    return 0
}

# ---------------------------------------------------------------------------------------
# Pack a directory, forcing the given paths to mode 0755.
#
# GNU tar's capital-X ("u+rwX") only preserves an executable bit that already exists, and a
# checkout on Windows has none -- the binary would arrive on Linux as 0644 and refuse to
# run. Archiving those paths in a second pass with an explicit mode makes the result
# correct no matter which host built it, while leaving everything else properly non-executable.
# ---------------------------------------------------------------------------------------
pack_tar() {
    local srcdir="$1" out="$2"
    shift 2
    local plain="${out%.gz}"
    local excludes=() rel

    for rel in "$@"; do
        excludes+=( "--exclude=$rel" )
    done

    rm -f "$plain" "$out"
    ( cd "$srcdir" && tar cf "$plain" --mode='u+rwX,go+rX' --owner=0 --group=0 \
        "${excludes[@]}" . )

    for rel in "$@"; do
        [ -f "$srcdir/$rel" ] || continue
        ( cd "$srcdir" && tar rf "$plain" --mode='0755' --owner=0 --group=0 "$rel" )
    done

    gzip -9 -n "$plain"
}

rid_for() {
    case "$1" in
        x64)   echo "linux-x64" ;;
        arm64) echo "linux-arm64" ;;
        arm)   echo "linux-arm" ;;
        *) echo "unknown arch: $1 (expected x64, arm64, arm or both)" >&2; exit 1 ;;
    esac
}

deb_arch_for() {
    case "$1" in
        x64)   echo "amd64" ;;
        arm64) echo "arm64" ;;
        arm)   echo "armhf" ;;
    esac
}

build_one() {
    local arch="$1"
    local rid stage prefix
    rid="$(rid_for "$arch")"
    stage="$OUT/stage-$arch"

    echo "==> publishing $rid"
    rm -rf "$stage" 2>/dev/null || true

    # The layout is the one a distribution would use: the program and its runtime under
    # /opt, and only a launcher, a desktop entry and icons under /usr.
    prefix="$stage/opt/$APP_ID"
    mkdir -p "$prefix" "$stage/usr/bin" "$stage/usr/share/applications"

    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:DebugType=none \
        -o "$prefix"

    chmod +x "$prefix/SparkPhotoDownloader" 2>/dev/null || true

    # A launcher rather than a symlink, so the program's own directory is the working
    # directory no matter where it is started from.
    cat > "$stage/usr/bin/$APP_ID" <<'LAUNCHER'
#!/bin/sh
exec /opt/spark-photo-downloader/SparkPhotoDownloader "$@"
LAUNCHER
    chmod +x "$stage/usr/bin/$APP_ID"

    cp "$ROOT/build/linux/$APP_ID.desktop" "$stage/usr/share/applications/"

    for size in 16 24 32 48 64 128 256 512; do
        local icon_dir="$stage/usr/share/icons/hicolor/${size}x${size}/apps"
        mkdir -p "$icon_dir"
        cp "$ASSETS/icons/$size.png" "$icon_dir/$APP_ID.png"
    done

    # ---- portable tarball ---------------------------------------------------------------
    cp "$ROOT/build/linux/install.sh" "$stage/install.sh"
    cp "$ROOT/build/linux/uninstall.sh" "$stage/uninstall.sh"
    cp "$ROOT/build/linux/measure-memory.sh" "$stage/measure-memory.sh"
    cp "$ROOT/README.md" "$stage/README.md"
    chmod +x "$stage/install.sh" "$stage/uninstall.sh" "$stage/measure-memory.sh"

    local tarball="$OUT/$APP_ID-$VERSION-linux-$arch.tar.gz"
    pack_tar "$stage" "$tarball" \
        "./opt/$APP_ID/SparkPhotoDownloader" \
        "./opt/$APP_ID/createdump" \
        "./usr/bin/$APP_ID" \
        ./install.sh ./uninstall.sh ./measure-memory.sh
    echo "==> tarball: $tarball"

    # ---- .deb ---------------------------------------------------------------------------
    if [[ "$MAKE_DEB" == true ]]; then
        local debarch work
        debarch="$(deb_arch_for "$arch")"
        work="$OUT/deb-$arch"
        rm -rf "$work"
        mkdir -p "$work/pkg"

        # Everything except the tarball-only helper files goes into the package.
        ( cd "$stage" && tar cf - opt usr ) | ( cd "$work/pkg" && tar xf - )

        local installed_kb
        installed_kb=$(du -sk "$work/pkg" | cut -f1)

        mkdir -p "$work/control"
        cat > "$work/control/control" <<EOF
Package: $APP_ID
Version: $VERSION
Section: graphics
Priority: optional
Architecture: $debarch
Maintainer: Spark Photo Downloader <noreply@example.com>
Installed-Size: $installed_kb
Depends: libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu69 | libicu67 | libicu66 | libicu-dev, libfontconfig1, libx11-6, libice6, libsm6
Recommends: ffmpeg
Description: Photo and video downloader for cameras and memory cards
 Copies photos and videos off cameras, phones, memory cards and folders,
 generating destination subfolders and file names from each shot's own
 metadata. Remembers every file it has downloaded, so nothing is fetched
 twice, and flags files whose downloaded copy has since been deleted.
EOF

        cat > "$work/control/postinst" <<'POSTINST'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
fi
POSTINST
        cp "$work/control/postinst" "$work/control/postrm"
        chmod +x "$work/control/postinst" "$work/control/postrm"

        pack_tar "$work/control" "$work/control.tar.gz" ./postinst ./postrm
        pack_tar "$work/pkg" "$work/data.tar.gz" \
            "./opt/$APP_ID/SparkPhotoDownloader" \
            "./opt/$APP_ID/createdump" \
            "./usr/bin/$APP_ID"
        printf '2.0\n' > "$work/debian-binary"

        local deb="$OUT/${APP_ID}_${VERSION}_${debarch}.deb"
        printf '!<arch>\n' > "$deb"
        ar_append "$deb" "debian-binary"  "$work/debian-binary"
        ar_append "$deb" "control.tar.gz" "$work/control.tar.gz"
        ar_append "$deb" "data.tar.gz"    "$work/data.tar.gz"
        rm -rf "$work"

        echo "==> package: $deb"
    fi

    # ---- AppImage, when the tooling is present -------------------------------------------
    if command -v appimagetool >/dev/null 2>&1; then
        local appdir="$OUT/AppDir-$arch"
        rm -rf "$appdir"
        mkdir -p "$appdir/usr"
        cp -R "$stage/opt/$APP_ID" "$appdir/usr/lib-app"
        cp -R "$stage/usr/share" "$appdir/usr/share"
        cp "$stage/usr/share/applications/$APP_ID.desktop" "$appdir/"
        cp "$ASSETS/icons/256.png" "$appdir/$APP_ID.png"

        cat > "$appdir/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib-app/SparkPhotoDownloader" "$@"
APPRUN
        chmod +x "$appdir/AppRun"

        ARCH_APPIMAGE="$( [ "$arch" = arm64 ] && echo aarch64 || echo x86_64 )" \
            appimagetool "$appdir" "$OUT/$APP_NAME-$VERSION-$arch.AppImage" >/dev/null
        rm -rf "$appdir"
        echo "==> AppImage: $OUT/$APP_NAME-$VERSION-$arch.AppImage"
    fi

    rm -rf "$stage" 2>/dev/null || true
}

# Clear previous output without insisting on removing the directory itself: on Windows an
# antivirus scanner routinely holds a handle on a freshly written archive for a while, and
# a failed rmdir should not abort the build.
mkdir -p "$OUT"
find "$OUT" -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null || true

if [[ "$ARCH" == "both" ]]; then
    build_one x64
    build_one arm64
else
    build_one "$ARCH"
fi

echo
ls -lh "$OUT"
