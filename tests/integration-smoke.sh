#!/usr/bin/env bash
# integration-smoke.sh — exercise a built railmark against tests/fixtures/sample.pdf.
#
# Usage:
#   ./tests/integration-smoke.sh <railmark command...>
#
# Examples:
#   ./tests/integration-smoke.sh dotnet run --project RailMark/ --
#   ./tests/integration-smoke.sh ./dist/railmark-0.9.0-linux-x86_64.AppImage
#   ./tests/integration-smoke.sh ./build/railmark-0.9.0-win-x64/railmark.exe
#
# The unit tests fake IPdfService/IPdfTextService, so nothing there touches real
# pdfium geometry. These checks do: quote resolution against a real text layer,
# annotation round-tripping through the PDF, and heading assignment driven by
# where headings actually sit on the page.

set -euo pipefail

if [ $# -eq 0 ]; then
    echo "Usage: $0 <railmark command...>" >&2
    exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FIXTURE="${REPO_ROOT}/tests/fixtures/sample.pdf"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

RAILMARK=("$@")
failures=0

check() {
    local label="$1" haystack="$2" needle="$3"
    if grep -qF -- "$needle" "$haystack"; then
        echo "  ok   ${label}"
    else
        echo "  FAIL ${label} — expected to find: ${needle}" >&2
        failures=$((failures + 1))
    fi
}

echo "Fixture:  ${FIXTURE}"
echo "Command:  ${RAILMARK[*]}"
echo ""

# --- 1. Export -------------------------------------------------------------
echo "[1/3] --export"
"${RAILMARK[@]}" "$FIXTURE" --export --no-vlm -o "${WORK}/export.md" > /dev/null

check "heading Introduction"   "${WORK}/export.md" "Introduction"
check "heading Methods"        "${WORK}/export.md" "Methods"
check "heading Results"        "${WORK}/export.md" "Results"
check "page 2 text present"    "${WORK}/export.md" "consistent across every run"
check "curly quotes preserved" "${WORK}/export.md" "“the best available option”"

# The fixture splits this word across a line break with a wrap hyphen. The
# export rejoins it but leaves U+0002 where the hyphen was — its own soft-hyphen
# marker — so the rendered word reads correctly but is not literally that
# string. Strip the markers before checking (TextLocator strips them too, which
# is why a quote copied out of this output still resolves).
tr -d '\002\003' < "${WORK}/export.md" > "${WORK}/export.stripped.md"
check "wrap hyphen rejoined"   "${WORK}/export.stripped.md" "interpretable in practice"

# --- 2. Apply markup -------------------------------------------------------
# Each quote is written the way an AI agent would write it, not the way the PDF
# stores it: across a wrap hyphen, and with straight quotes for curly ones.
echo ""
echo "[2/3] --apply-markup"
cat > "${WORK}/plan.json" <<'PLAN'
{"entries":[
 {"page":1,"quote":"interpretable in practice","type":"highlight","comment":"spans a wrap hyphen"},
 {"page":1,"quote":"\"the best available option\"","type":"underline","comment":"straight quotes vs curly"},
 {"page":1,"quote":"paragraph sits under the second heading","type":"strikeout","comment":"under Methods"},
 {"page":2,"quote":"small but consistent","type":"squiggly"}
]}
PLAN

"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/plan.json" \
    -o "${WORK}/marked.pdf" > "${WORK}/apply.json" 2>/dev/null

# Every entry must resolve. A regression in quote normalisation shows up here
# as a "Quote not found" error rather than a crash, so check explicitly.
if grep -qF '"success":false' "${WORK}/apply.json"; then
    echo "  FAIL some markup entries did not resolve:" >&2
    cat "${WORK}/apply.json" >&2
    failures=$((failures + 1))
else
    echo "  ok   all 4 entries resolved"
fi
# The highlight spans two visual lines, so it must produce two quad spans.
check "wrap-hyphen highlight spans 2 lines" "${WORK}/apply.json" '"spanCount":2'

# --- 3. Extract the annotations back ---------------------------------------
echo ""
echo "[3/3] annotation extraction"
"${RAILMARK[@]}" "${WORK}/marked.pdf" -o "${WORK}/annots.md" > /dev/null

check "4 annotations found"     "${WORK}/annots.md" "**4 annotations**"
# The highlight spans a wrap hyphen. Its text is extracted one rect per visual
# line, and the parts must be rejoined via the page text — joining them with a
# space would render "inter pretable".
check "wrap-hyphen highlight rejoined" "${WORK}/annots.md" "interpretable in practice"
if grep -qF "inter pretable" "${WORK}/annots.md"; then
    echo "  FAIL wrap-hyphen highlight rendered as \"inter pretable\"" >&2
    failures=$((failures + 1))
else
    echo "  ok   no split-word artefact"
fi
check "strikeout rendered"      "${WORK}/annots.md" "~~paragraph sits under the second heading~~"
check "strikeout labelled"      "${WORK}/annots.md" "suggested deletion"
# Marked-up text goes through CleanText, which normalises the PDF's curly
# quotes to straight ones — so the annotation output differs from the export.
check "underline rendered"      "${WORK}/annots.md" '<u>"the best available option"</u>'
check "squiggly rendered"       "${WORK}/annots.md" "small but consistent"
# Page 1 carries two headings; the annotation below the second must file under
# it rather than under the first. This is the whole point of heading positions.
check "section Introduction"    "${WORK}/annots.md" "## Introduction"
check "section Methods"         "${WORK}/annots.md" "## Methods"
if awk '/^## Methods/{m=1} m && /paragraph sits under the second heading/{found=1} END{exit !found}' "${WORK}/annots.md"; then
    echo "  ok   annotation filed under the heading above it"
else
    echo "  FAIL annotation below the second heading was not filed under it" >&2
    failures=$((failures + 1))
fi

echo ""
if [ "$failures" -ne 0 ]; then
    echo "${failures} check(s) failed." >&2
    exit 1
fi
echo "All integration checks passed."
