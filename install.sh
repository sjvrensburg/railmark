#!/usr/bin/env bash
set -euo pipefail

VERSION="0.7.0"
INSTALL_DIR="${XDG_BIN_HOME:-$HOME/.local/bin}"
ASSET="railmark-${VERSION}-linux-x86_64.AppImage"
URL="https://github.com/sjvrensburg/railmark/releases/download/v${VERSION}/${ASSET}"

bold='\033[1m'
green='\033[0;32m'
yellow='\033[0;33m'
reset='\033[0m'

info()  { echo -e "${bold}railmark install:${reset} $*"; }
ok()    { echo -e "${green}✔${reset} $*"; }
warn()  { echo -e "${yellow}⚠${reset} $*"; }

mkdir -p "$INSTALL_DIR"

# --- railmark ---
# Self-contained AppImage — bundles the .NET runtime and all native libraries
# (libpdfium, libSkiaSharp, libonnxruntime). No external CLI or config required.

if [ -x "$INSTALL_DIR/railmark" ]; then
    warn "railmark already installed. Reinstalling v${VERSION}."
fi

info "Downloading railmark v${VERSION}..."
curl -fsSL "$URL" -o "$INSTALL_DIR/railmark"
chmod +x "$INSTALL_DIR/railmark"
ok "railmark v${VERSION} → $INSTALL_DIR/railmark"

# --- PATH check ---

if echo ":$PATH:" | grep -q ":$INSTALL_DIR:"; then
    ok "$INSTALL_DIR is on PATH"
else
    echo ""
    warn "$INSTALL_DIR is not on PATH. Add to your shell profile:"
    echo ""
    echo "    export PATH=\"$INSTALL_DIR:\$PATH\""
    echo ""
fi

ok "Done! Usage: railmark <pdf>"
