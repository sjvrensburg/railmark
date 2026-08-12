#!/usr/bin/env bash
# download-model.sh — Fetch the ONNX layout model that release builds bundle.
#
# Usage:
#   ./scripts/download-model.sh [output-dir]
#
# Writes PP-DocLayoutV3.onnx into <output-dir> (default: ./models) and verifies
# its SHA-256. Re-running is cheap: an existing file with the right digest is
# left alone.
#
# Release artifacts bundle this model so that `--export` gets layout analysis
# out of the box; without it, export falls back to plain text.

set -euo pipefail

MODEL_NAME="PP-DocLayoutV3.onnx"
MODEL_URL="https://huggingface.co/alex-dinh/PP-DocLayoutV3-ONNX/resolve/main/${MODEL_NAME}"
# Pinned so a silently-changed upstream file fails the build instead of
# shipping unnoticed inside a release.
MODEL_SHA256="d24809294b2f9f1a9a2767043a64df2714b66e5be056887be2233d1117d784f6"

OUTPUT_DIR="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/models}"
OUTPUT_PATH="${OUTPUT_DIR}/${MODEL_NAME}"

verify() {
    local actual
    actual="$(sha256sum "$1" | cut -d' ' -f1)"
    [[ "$actual" == "$MODEL_SHA256" ]]
}

mkdir -p "$OUTPUT_DIR"

if [[ -f "$OUTPUT_PATH" ]] && verify "$OUTPUT_PATH"; then
    echo "Model already present and verified: ${OUTPUT_PATH}"
    exit 0
fi

echo "Downloading ${MODEL_NAME} (~124 MiB)..."
curl -fL --retry 3 --retry-delay 5 -o "${OUTPUT_PATH}.tmp" "$MODEL_URL"

if ! verify "${OUTPUT_PATH}.tmp"; then
    echo "Error: SHA-256 mismatch for ${MODEL_NAME}." >&2
    echo "  expected: ${MODEL_SHA256}" >&2
    echo "  actual:   $(sha256sum "${OUTPUT_PATH}.tmp" | cut -d' ' -f1)" >&2
    rm -f "${OUTPUT_PATH}.tmp"
    exit 1
fi

mv "${OUTPUT_PATH}.tmp" "$OUTPUT_PATH"
echo "Verified: ${OUTPUT_PATH}"
