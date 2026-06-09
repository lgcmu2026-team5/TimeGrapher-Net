#!/usr/bin/env bash
#
# TimeGrapher — Raspberry Pi 5 (linux-arm64) installer.
#
# Run from inside the extracted release folder:
#     ./install.sh
#
# What it does:
#   1. Installs the X11/font runtime libraries Avalonia needs (apt; skip with --no-deps).
#   2. Marks the TimeGrapher.App launcher executable.
#   3. Installs the taskbar icon and a desktop entry whose Exec/Icon point at THIS
#      folder (no hardcoded paths), so the app shows up in the menu/taskbar.
#
# Re-runnable (idempotent). ICU (libicu) is intentionally NOT a dependency: the app
# is built with InvariantGlobalization, so .NET does not require system ICU.
set -euo pipefail

APP_NAME="TimeGrapher.App"
ICON_SRC="AppIcon-256.png"
INSTALL_DEPS=1

usage() {
  cat <<'USAGE'
Usage: ./install.sh [--no-deps] [--help]

  --no-deps   Skip the apt package install (use if the runtime libraries are
              already present, or on a non-Debian/non-Pi system).
  --help      Show this help.
USAGE
}

for arg in "$@"; do
  case "$arg" in
    --no-deps) INSTALL_DEPS=0 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; usage >&2; exit 2 ;;
  esac
done

# Resolve the folder this script (and the app) live in, as an absolute path.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LAUNCHER="$SCRIPT_DIR/$APP_NAME"

if [ ! -f "$LAUNCHER" ]; then
  echo "error: $APP_NAME not found next to this script ($SCRIPT_DIR)." >&2
  echo "       Run install.sh from inside the extracted release folder." >&2
  exit 1
fi

# --- 1. Runtime dependencies -------------------------------------------------
if [ "$INSTALL_DEPS" -eq 1 ]; then
  if command -v apt-get >/dev/null 2>&1; then
    SUDO=""
    if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi
    echo "==> Installing runtime libraries (X11, fontconfig, XWayland)..."
    $SUDO apt-get update
    $SUDO apt-get install -y libx11-6 libice6 libsm6 libfontconfig1 xwayland
  else
    echo "==> apt-get not found; skipping dependency install."
    echo "    Install the equivalents of: libx11-6 libice6 libsm6 libfontconfig1 xwayland"
  fi
else
  echo "==> Skipping dependency install (--no-deps)."
fi

# --- 2. Executable bit -------------------------------------------------------
echo "==> Marking $APP_NAME executable..."
chmod +x "$LAUNCHER"

# --- 3. Desktop integration (icon + .desktop) --------------------------------
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}"
ICON_DIR="$DATA_DIR/icons"
APPS_DIR="$DATA_DIR/applications"
mkdir -p "$ICON_DIR" "$APPS_DIR"

ICON_DEST=""
if [ -f "$SCRIPT_DIR/$ICON_SRC" ]; then
  ICON_DEST="$ICON_DIR/timegrapher.png"
  cp "$SCRIPT_DIR/$ICON_SRC" "$ICON_DEST"
  echo "==> Installed icon -> $ICON_DEST"
else
  echo "==> $ICON_SRC not found; desktop entry will have no icon."
fi

# Generate the desktop entry fresh so Exec/Icon point at the real install location
# (no hardcoded paths). The filename must equal the app-id (StartupWMClass) so the
# Pi panel matches the running window to this entry's icon.
DESKTOP_DEST="$APPS_DIR/$APP_NAME.desktop"
echo "==> Writing desktop entry -> $DESKTOP_DEST"
{
  echo "[Desktop Entry]"
  echo "Type=Application"
  echo "Name=TimeGrapher"
  echo "Exec=$LAUNCHER"
  if [ -n "$ICON_DEST" ]; then echo "Icon=$ICON_DEST"; fi
  echo "StartupWMClass=$APP_NAME"
  echo "Categories=Utility;"
} > "$DESKTOP_DEST"
chmod 644 "$DESKTOP_DEST"

# Best-effort cache refresh so the entry/icon appear without a re-login.
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f "$ICON_DIR" >/dev/null 2>&1 || true
fi

echo
echo "Done. Launch 'TimeGrapher' from the menu, or run:"
echo "    $LAUNCHER"
echo "Headless self-check (no GUI):"
echo "    $LAUNCHER --smoke"
