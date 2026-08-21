#!/bin/sh
# Install Spark Photo Downloader from the portable tarball.
#
#   ./install.sh            install for the current user only (no root needed)
#   sudo ./install.sh -s    install system-wide for everyone
#
# Both put the program's own files in one directory and add a desktop entry and icons, so
# uninstalling is just removing those again -- see uninstall.sh.

set -eu

APP_ID="spark-photo-downloader"
HERE="$(cd "$(dirname "$0")" && pwd)"

SYSTEM=false
[ "${1:-}" = "-s" ] || [ "${1:-}" = "--system" ] && SYSTEM=true

if [ "$SYSTEM" = true ]; then
    [ "$(id -u)" -eq 0 ] || { echo "a system-wide install needs root: sudo $0 -s" >&2; exit 1; }
    PROGRAM_DIR="/opt/$APP_ID"
    BIN_DIR="/usr/local/bin"
    SHARE_DIR="/usr/share"
else
    PROGRAM_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/$APP_ID"
    BIN_DIR="$HOME/.local/bin"
    SHARE_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
fi

echo "installing to $PROGRAM_DIR"

rm -rf "$PROGRAM_DIR"
mkdir -p "$PROGRAM_DIR" "$BIN_DIR" "$SHARE_DIR/applications"
cp -R "$HERE/opt/$APP_ID/." "$PROGRAM_DIR/"
chmod +x "$PROGRAM_DIR/SparkPhotoDownloader"

cat > "$BIN_DIR/$APP_ID" <<LAUNCHER
#!/bin/sh
exec "$PROGRAM_DIR/SparkPhotoDownloader" "\$@"
LAUNCHER
chmod +x "$BIN_DIR/$APP_ID"

# The shipped desktop entry points at /usr/bin; rewrite it to wherever the launcher landed.
sed "s|^Exec=.*|Exec=$BIN_DIR/$APP_ID %U|" \
    "$HERE/usr/share/applications/$APP_ID.desktop" \
    > "$SHARE_DIR/applications/$APP_ID.desktop"

for size in 16 24 32 48 64 128 256 512; do
    dir="$SHARE_DIR/icons/hicolor/${size}x${size}/apps"
    src="$HERE/usr/share/icons/hicolor/${size}x${size}/apps/$APP_ID.png"
    [ -f "$src" ] || continue
    mkdir -p "$dir"
    cp "$src" "$dir/$APP_ID.png"
done

command -v update-desktop-database >/dev/null 2>&1 &&
    update-desktop-database -q "$SHARE_DIR/applications" 2>/dev/null || true
command -v gtk-update-icon-cache >/dev/null 2>&1 &&
    gtk-update-icon-cache -q -f "$SHARE_DIR/icons/hicolor" 2>/dev/null || true

echo "installed."
echo "  launch from your applications menu, or run: $APP_ID"

case ":$PATH:" in
    *":$BIN_DIR:"*) ;;
    *) echo "  note: $BIN_DIR is not on your PATH" ;;
esac
