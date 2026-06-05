#!/usr/bin/env bash
set -euo pipefail

VERSION="0.5.1"
INSTALL_DIR="${XDG_BIN_HOME:-$HOME/.local/bin}"
RR2_VERSION="v3.23.0.0"

bold='\033[1m'
green='\033[0;32m'
yellow='\033[0;33m'
reset='\033[0m'

info()  { echo -e "${bold}rr2annotate install:${reset} $*"; }
ok()    { echo -e "${green}✔${reset} $*"; }
warn()  { echo -e "${yellow}⚠${reset} $*"; }

mkdir -p "$INSTALL_DIR"

# --- rr2annotate ---

if [ -x "$INSTALL_DIR/rr2annotate" ]; then
    warn "rr2annotate already installed. Reinstalling v${VERSION}."
fi

info "Downloading rr2annotate v${VERSION}..."
curl -fsSL "https://github.com/sjvrensburg/rr2parser/releases/download/v${VERSION}/rr2annotate-${VERSION}-linux-x64.tar.gz" \
    | tar xz -C "$INSTALL_DIR" Rr2Annotate libSkiaSharp.so

chmod +x "$INSTALL_DIR/Rr2Annotate"
ln -sf "$INSTALL_DIR/Rr2Annotate" "$INSTALL_DIR/rr2annotate"
ok "rr2annotate v${VERSION} → $INSTALL_DIR/rr2annotate"

# --- RailReader2 CLI ---

if [ -x "$INSTALL_DIR/railreader2-cli" ]; then
    warn "RailReader2 CLI already installed. Skipping."
else
    info "Downloading RailReader2 CLI ${RR2_VERSION}..."
    curl -fsSL "https://github.com/sjvrensburg/railreader2/releases/download/${RR2_VERSION}/railreader2-cli-linux-x64.tar.gz" \
        | tar xz -C "$INSTALL_DIR"
    ln -sf "$INSTALL_DIR/RailReader2.Cli" "$INSTALL_DIR/railreader2-cli"
    ok "RailReader2 CLI → $INSTALL_DIR/railreader2-cli"
fi

# --- Write config so rr2annotate doesn't prompt on first run ---

CONFIG_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/rr2annotate"
mkdir -p "$CONFIG_DIR"
cat > "$CONFIG_DIR/settings.json" <<EOF
{"CliCommand":"$INSTALL_DIR/railreader2-cli"}
EOF
ok "Config written to $CONFIG_DIR/settings.json"

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

ok "Done! Usage: rr2annotate <pdf>"
