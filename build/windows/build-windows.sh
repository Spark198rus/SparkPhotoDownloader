#!/usr/bin/env bash
# Build Spark Photo Downloader for Windows.
#
#   ./build/windows/build-windows.sh                # x64
#   ./build/windows/build-windows.sh --arch arm64   # ARM64
#   ./build/windows/build-windows.sh --arch both
#
# Produces a self-contained .zip per architecture. Unlike the macOS and Linux packages there
# is no permission bit to preserve, so a plain zip is the right container here.

set -euo pipefail

ARCH="x64"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --arch) ARCH="$2"; shift 2 ;;
        -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/SparkPhotoDownloader2.csproj"
OUT="$ROOT/artifacts/windows"
VERSION="1.0.1"

mkdir -p "$OUT"
find "$OUT" -mindepth 1 -maxdepth 1 -exec rm -rf {} + 2>/dev/null || true

build_one() {
    local arch="$1"
    local stage="$OUT/SparkPhotoDownloader-$VERSION-win-$arch"

    echo "==> publishing win-$arch"
    dotnet publish "$PROJECT" \
        -c Release \
        -r "win-$arch" \
        --self-contained true \
        -p:DebugType=none \
        -o "$stage"

    cp "$ROOT/README.md" "$stage/README.md"
    cp "$ROOT/LICENSE" "$stage/LICENSE"

    local zip="$OUT/SparkPhotoDownloader-$VERSION-win-$arch.zip"
    rm -f "$zip"

    # Prefer the zip that ships with Windows; fall back to Git Bash's if it is absent.
    if command -v powershell >/dev/null 2>&1; then
        powershell -NoProfile -Command \
            "Compress-Archive -Path '$(cygpath -w "$stage")\\*' -DestinationPath '$(cygpath -w "$zip")' -CompressionLevel Optimal" \
            >/dev/null
    else
        ( cd "$OUT" && zip -qr "$zip" "$(basename "$stage")" )
    fi

    rm -rf "$stage"
    echo "==> archive: $zip"
}

if [[ "$ARCH" == "both" ]]; then
    build_one x64
    build_one arm64
else
    build_one "$ARCH"
fi

echo
ls -lh "$OUT"
