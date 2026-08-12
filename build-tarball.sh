#!/usr/bin/env bash
# build-tarball.sh — Build a self-contained railmark tarball for Linux x86-64.
#
# The AppImage is the recommended install, but it needs a kernel with FUSE (or
# --appimage-extract-and-run). This tarball is the fallback for containers,
# locked-down CI images and distros where that is awkward: same binary, same
# bundled native libraries, plain directory layout.
#
# Usage:
#   ./build-tarball.sh [--include-model <model-file>]
#
# Options:
#   --include-model <path>  Bundle an ONNX layout model (see scripts/download-model.sh).
#                           If omitted, export mode works without layout
#                           analysis (plain text fallback).
#
# Prerequisites:
#   - .NET 10 SDK

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="${SCRIPT_DIR}/RailMark"
OUTPUT_DIR="${SCRIPT_DIR}/dist"

INCLUDE_MODEL=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --include-model)
            INCLUDE_MODEL="$2"; shift 2 ;;
        *) echo "Unknown argument: $1"; exit 1 ;;
    esac
done

VERSION=$(grep '<Version>' "${PROJECT_DIR}/RailMark.csproj" | sed 's/.*<Version>\(.*\)<\/Version>.*/\1/' | tr -d '[:space:]')
STAGE_NAME="railmark-${VERSION}-linux-x86_64"
STAGE="${SCRIPT_DIR}/build/${STAGE_NAME}"

echo "=== Building railmark tarball ==="
echo "  Version: ${VERSION}"
echo "  Staging: ${STAGE}"
echo ""

echo "[1/4] Scaffolding..."
rm -rf "${STAGE:?}"
mkdir -p "${STAGE}/bin"

echo "[2/4] Publishing self-contained linux-x64 binary..."
dotnet publish "${PROJECT_DIR}" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -o "${STAGE}/bin"

# Everything (binary + .so libs) stays in bin/ — the .NET host requires its own
# libraries next to the binary. The launcher below adds bin/ to LD_LIBRARY_PATH
# so third-party natives (libpdfium, libSkiaSharp, libonnxruntime) resolve too.

if [[ -n "$INCLUDE_MODEL" ]]; then
    if [[ ! -f "$INCLUDE_MODEL" ]]; then
        echo "Warning: model file not found: ${INCLUDE_MODEL}" >&2
    else
        echo "[3/4] Bundling model: $(basename "${INCLUDE_MODEL}")"
        mkdir -p "${STAGE}/models"
        cp "${INCLUDE_MODEL}" "${STAGE}/models/"
    fi
else
    echo "[3/4] No model bundled."
fi

cp "${SCRIPT_DIR}/README.md" "${SCRIPT_DIR}/LICENSE" "${STAGE}/"

# Launcher mirrors AppImage/AppRun: same LD_LIBRARY_PATH and APPDIR contract, so
# LayoutModelLocator finds models/ the same way it does inside the AppImage.
cat > "${STAGE}/railmark" <<'LAUNCHER'
#!/usr/bin/env bash
HERE="$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")"
export LD_LIBRARY_PATH="${HERE}/bin:${LD_LIBRARY_PATH:-}"
export APPDIR="${HERE}"
exec "${HERE}/bin/railmark" "$@"
LAUNCHER
chmod +x "${STAGE}/railmark"

echo "[4/4] Creating tarball..."
mkdir -p "${OUTPUT_DIR}"
OUTPUT_FILE="${OUTPUT_DIR}/${STAGE_NAME}.tar.gz"
tar -czf "${OUTPUT_FILE}" -C "${SCRIPT_DIR}/build" "${STAGE_NAME}"

echo ""
echo "Done."
echo "  Output: ${OUTPUT_FILE}"
echo "  Size:   $(du -sh "${OUTPUT_FILE}" | cut -f1)"
echo ""
echo "Install:"
echo "  tar -xzf '${OUTPUT_FILE}' -C ~/.local/opt/"
echo "  ln -sf ~/.local/opt/${STAGE_NAME}/railmark ~/.local/bin/railmark"
