#!/usr/bin/env bash
# build-appimage.sh — Build a self-contained rr2annotate AppImage for Linux x86-64.
#
# Usage:
#   ./build-appimage.sh [--include-model <model-file>]
#
# Options:
#   --include-model <path>  Bundle an ONNX layout model inside the AppImage.
#                           The model is looked up from the models/ directory in
#                           the RailReader2 CLI installation, or you can pass an
#                           explicit path. If omitted, export mode works without
#                           layout analysis (plain text fallback).
#
# Prerequisites:
#   - .NET 10 SDK
#   - ~/bin/appimagetool-x86_64.AppImage (or appimagetool in PATH)
#   - FUSE (for appimagetool); if unavailable use APPIMAGETOOL_SIGN_ENV

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="${SCRIPT_DIR}/Rr2Annotate"
APPDIR="${SCRIPT_DIR}/build/Rr2Annotate.AppDir"
OUTPUT_DIR="${SCRIPT_DIR}/dist"

APPIMAGETOOL="${APPIMAGETOOL:-}"
INCLUDE_MODEL=""

# Parse args
while [[ $# -gt 0 ]]; do
    case "$1" in
        --include-model)
            INCLUDE_MODEL="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

# Find appimagetool
if [[ -z "$APPIMAGETOOL" ]]; then
    if command -v appimagetool &>/dev/null; then
        APPIMAGETOOL="$(command -v appimagetool)"
    elif [[ -f "${HOME}/bin/appimagetool-x86_64.AppImage" ]]; then
        APPIMAGETOOL="${HOME}/bin/appimagetool-x86_64.AppImage"
    else
        echo "Error: appimagetool not found. Set APPIMAGETOOL env var or place it in ~/bin/." >&2
        exit 1
    fi
fi

echo "=== Building rr2annotate AppImage ==="
echo "  Project:     ${PROJECT_DIR}"
echo "  AppDir:      ${APPDIR}"
echo "  Output:      ${OUTPUT_DIR}"
echo "  appimagetool: ${APPIMAGETOOL}"
echo ""

# 1. Scaffold AppDir (clean slate)
echo "[1/4] Scaffolding AppDir..."
rm -rf "${APPDIR:?}"
mkdir -p "${APPDIR}/usr/bin"

# 2. Publish self-contained binary
echo "[2/4] Publishing self-contained linux-x64 binary..."
dotnet publish "${PROJECT_DIR}" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -o "${APPDIR}/usr/bin"

# Keep all files (including .so libs) in usr/bin/ — the .NET host requires
# its own libraries (libhostpolicy.so, libcoreclr.so, etc.) to sit next to
# the binary, so we cannot move them elsewhere. AppRun adds usr/bin/ to
# LD_LIBRARY_PATH so third-party native libs (libpdfium, libSkiaSharp, etc.)
# are also discoverable.

# appimagetool wants the desktop file and icon at the top level of AppDir.
cp "${SCRIPT_DIR}/AppImage/rr2annotate.desktop" "${APPDIR}/"
cp "${SCRIPT_DIR}/AppImage/AppRun"              "${APPDIR}/"
chmod +x "${APPDIR}/AppRun"

# Icon: use the RailReader2 icon if available, else create a minimal placeholder.
ICON_SRC="${HOME}/bin/railreader2.png"
if [[ -f "$ICON_SRC" ]]; then
    cp "$ICON_SRC" "${APPDIR}/rr2annotate.png"
else
    # Tiny 16x16 placeholder so appimagetool doesn't complain
    python3 -c "
import struct, zlib
def png_chunk(name, data):
    c = zlib.crc32(name + data) & 0xffffffff
    return struct.pack('>I', len(data)) + name + data + struct.pack('>I', c)
sig = b'\x89PNG\r\n\x1a\n'
ihdr = png_chunk(b'IHDR', struct.pack('>IIBBBBB', 16, 16, 8, 2, 0, 0, 0))
row = b'\x00' + b'\x80\x80\x80' * 16
raw = b''.join(b'\x00' + b'\x80\x80\x80' * 16 for _ in range(16))
idat = png_chunk(b'IDAT', zlib.compress(b''.join(b'\x00' + b'\x80\x80\x80' * 16 for _ in range(16))))
iend = png_chunk(b'IEND', b'')
with open('${APPDIR}/rr2annotate.png', 'wb') as f:
    f.write(sig + ihdr + idat + iend)
" 2>/dev/null || touch "${APPDIR}/rr2annotate.png"
fi

# 3. Bundle optional ONNX model
if [[ -n "$INCLUDE_MODEL" ]]; then
    if [[ ! -f "$INCLUDE_MODEL" ]]; then
        echo "Warning: model file not found: ${INCLUDE_MODEL}" >&2
    else
        echo "[3b] Bundling model: $(basename "${INCLUDE_MODEL}")"
        mkdir -p "${APPDIR}/models"
        cp "${INCLUDE_MODEL}" "${APPDIR}/models/"
    fi
fi

# 4. Build the AppImage
echo "[4/4] Building AppImage..."
mkdir -p "${OUTPUT_DIR}"
VERSION=$(grep '<Version>' "${PROJECT_DIR}/Rr2Annotate.csproj" | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/' | tr -d '[:space:]')
OUTPUT_FILE="${OUTPUT_DIR}/rr2annotate-${VERSION}-linux-x86_64.AppImage"

ARCH=x86_64 "${APPIMAGETOOL}" "${APPDIR}" "${OUTPUT_FILE}" 2>&1

echo ""
echo "Done."
echo "  Output: ${OUTPUT_FILE}"
echo "  Size:   $(du -sh "${OUTPUT_FILE}" | cut -f1)"
echo ""
echo "Install by copying to ~/bin/ or any directory in \$PATH:"
echo "  cp '${OUTPUT_FILE}' ~/bin/rr2annotate"
echo "  chmod +x ~/bin/rr2annotate"
