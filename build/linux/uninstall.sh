#!/bin/sh
# Remove Spark Photo Downloader.
#
#   ./uninstall.sh          remove a per-user install
#   sudo ./uninstall.sh -s  remove a system-wide install
#
# Your download history, settings and thumbnail cache are left alone. To remove those too,
# delete ${XDG_DATA_HOME:-$HOME/.local/share}/SparkPhotoDownloader.

set -eu

APP_ID="spark-photo-downloader"

SYSTEM=false
[ "${1:-}" = "-s" ] || [ "${1:-}" = "--system" ] && SYSTEM=true

if [ "$SYSTEM" = true ]; then
    [ "$(id -u)" -eq 0 ] || { echo "removing a system-wide install needs root: sudo $0 -s" >&2; exit 1; }
    PROGRAM_DIR="/opt/$APP_ID"
    BIN_DIR="/usr/local/bin"
    SHARE_DIR="/usr/share"
else
    PROGRAM_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/$APP_ID"
    BIN_DIR="$HOME/.local/bin"
    SHARE_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
fi

rm -rf "$PROGRAM_DIR"
rm -f "$BIN_DIR/$APP_ID"
rm -f "$SHARE_DIR/applications/$APP_ID.desktop"

for size in 16 24 32 48 64 128 256 512; do
    rm -f "$SHARE_DIR/icons/hicolor/${size}x${size}/apps/$APP_ID.png"
done

command -v update-desktop-database >/dev/null 2>&1 &&
    update-desktop-database -q "$SHARE_DIR/applications" 2>/dev/null || true

echo "removed. Your download history and settings were kept."
