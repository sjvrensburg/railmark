#!/usr/bin/env bash
# integration-smoke.sh — exercise a built railmark against tests/fixtures/sample.pdf.
#
# Usage:
#   ./tests/integration-smoke.sh <railmark command...>
#
# Examples:
#   ./tests/integration-smoke.sh dotnet run --project RailMark/ --
#   ./tests/integration-smoke.sh ./dist/railmark-<version>-linux-x86_64.AppImage
#   ./tests/integration-smoke.sh ./build/railmark-<version>-win-x64/railmark.exe
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

# Assert a jq expression holds for a JSON file.
jcheck() {
    local label="$1" file="$2" expr="$3"
    if jq -e "$expr" "$file" > /dev/null 2>&1; then
        echo "  ok   ${label}"
    else
        echo "  FAIL ${label} — expected: ${expr}" >&2
        failures=$((failures + 1))
    fi
}

# Assert a file does NOT contain a string.
nocheck() {
    local label="$1" haystack="$2" needle="$3"
    if grep -qF -- "$needle" "$haystack"; then
        echo "  FAIL ${label} — did not expect: ${needle}" >&2
        failures=$((failures + 1))
    else
        echo "  ok   ${label}"
    fi
}

section_no=0
section() {
    section_no=$((section_no + 1))
    echo "[${section_no}] $1"
}

echo "Fixture:  ${FIXTURE}"
echo "Command:  ${RAILMARK[*]}"
echo ""

# --- 1. Export -------------------------------------------------------------
section "--export"
"${RAILMARK[@]}" "$FIXTURE" --export --no-vlm -o "${WORK}/export.md" > /dev/null

check "heading Introduction"   "${WORK}/export.md" "Introduction"
check "heading Methods"        "${WORK}/export.md" "Methods"
check "heading Results"        "${WORK}/export.md" "Results"
check "page 2 text present"    "${WORK}/export.md" "consistent across every run"
check "curly quotes preserved" "${WORK}/export.md" "“the best available option”"

# The fixture splits this word across a line break with a wrap hyphen, and the
# export must rejoin it. Up to RailReader.Core 0.56.0 the output carried U+0002
# where the hyphen had been (RailReaderCore#101) and this had to be stripped
# before comparing; 0.56.1 emits the joined word, so assert on it directly.
check "wrap hyphen rejoined"   "${WORK}/export.md" "interpretable in practice"
if grep -qP '[\x02\x03]' "${WORK}/export.md"; then
    echo "  FAIL export output contains soft-hyphen markers (RailReaderCore#101 regression)" >&2
    failures=$((failures + 1))
else
    echo "  ok   export output carries no invisible markers"
fi

# --- 2. Apply markup -------------------------------------------------------
# Each quote is written the way an AI agent would write it, not the way the PDF
# stores it: across a wrap hyphen, and with straight quotes for curly ones.
echo ""
section "--apply-markup"
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
section "annotation extraction"
"${RAILMARK[@]}" "${WORK}/marked.pdf" -o "${WORK}/annots.md" > /dev/null

# The fixture ships 4 annotations of its own (a square and three ink strokes),
# and the plan above adds 4 more.
check "8 annotations found"     "${WORK}/annots.md" "**8 annotations**"
check "freehand grouped under its heading" "${WORK}/annots.md" "| Results | 1 | 0 | 0 | 3 | 0 |"
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


# --- 4. Screenshots ---------------------------------------------------------
# The fixture carries a /Square over the "Introduction" heading and three /Ink
# strokes on page 2 — two within the 50pt merge distance, one far away.
echo ""
section "--images"
"${RAILMARK[@]}" "$FIXTURE" --images -o "${WORK}/shots.md" > /dev/null

shot_dir="${WORK}/shots-images"
if [ -d "$shot_dir" ]; then
    echo "  ok   image directory created"
else
    echo "  FAIL no image directory at ${shot_dir}" >&2
    failures=$((failures + 1))
fi

# Four annotations, but the two near strokes share a crop, so three files.
shot_count=$(find "$shot_dir" -name '*.png' 2>/dev/null | wc -l)
if [ "$shot_count" -eq 3 ]; then
    echo "  ok   4 annotations produced 3 images (near strokes share one)"
else
    echo "  FAIL expected 3 images from 4 annotations, got ${shot_count}" >&2
    failures=$((failures + 1))
fi

check "images referenced in Markdown" "${WORK}/shots.md" "shots-images/annotation_001.png"

# A crop from the wrong part of the page lands on blank paper. Every annotation
# here sits over text, so every crop must carry real content — this is what
# catches a coordinate regression (issue #22) in the assembled artifact.
blank=0
for png in "$shot_dir"/*.png; do
    [ -e "$png" ] || continue
    size=$(wc -c < "$png")
    if [ "$size" -lt 2000 ]; then
        echo "  FAIL $(basename "$png") is ${size} bytes — looks like a blank crop" >&2
        blank=$((blank + 1))
    fi
done
if [ "$blank" -eq 0 ]; then
    echo "  ok   no blank crops"
else
    failures=$((failures + blank))
fi

# --- 5. Picking an occurrence ------------------------------------------------
# "run of the experiment" occurs twice on page 2. An entry that doesn't say
# which one it means must be flagged, and one that does must land on it.
echo ""
section "occurrence and context"
cat > "${WORK}/occ.json" <<'PLAN'
{"entries":[
 {"page":2,"quote":"run of the experiment","type":"highlight","occurrence":2},
 {"page":2,"quote":"run of the experiment","type":"underline"},
 {"page":2,"quote":"run of the experiment","type":"squiggly","contextBefore":"every"},
 {"page":2,"quote":"run of the experiment","type":"strikeout","occurrence":3}
]}
PLAN

set +e
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/occ.json" --dry-run > "${WORK}/occ-dry.json" 2> "${WORK}/occ-dry.err"
status=$?
set -e
if [ "$status" -eq 2 ]; then
    echo "  ok   out-of-range occurrence fails the plan (exit 2)"
else
    echo "  FAIL expected exit 2 for an out-of-range occurrence, got ${status}" >&2
    failures=$((failures + 1))
fi
jcheck "both occurrences counted"          "${WORK}/occ-dry.json" 'all(.[]; .matchCount == 2)'
jcheck "explicit occurrence not flagged"   "${WORK}/occ-dry.json" '.[0].success and (.[0] | has("warning") | not)'
jcheck "ambiguous quote flagged"           "${WORK}/occ-dry.json" '.[1].success and (.[1].warning | startswith("Ambiguous"))'
jcheck "context picks a unique match"      "${WORK}/occ-dry.json" '.[2].success and (.[2] | has("warning") | not)'
jcheck "occurrence 3 of 2 fails"           "${WORK}/occ-dry.json" '(.[3].success | not) and (.[3].error | contains("only 2 times"))'
jcheck "dry run shows the second match"    "${WORK}/occ-dry.json" '.[0].context | contains("A second [[run of the experiment]]")'
jcheck "context picks the first match"     "${WORK}/occ-dry.json" '.[2].context | contains("every [[run of the experiment]]")'
check  "ambiguity warned on stderr"        "${WORK}/occ-dry.err" "WARNING page 2"

# Apply for real, then check where the highlight actually sits: the extracted
# Markdown bolds the marked occurrence within the page text.
set +e
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/occ.json" -o "${WORK}/occ.pdf" > "${WORK}/occ.json.out" 2>/dev/null
set -e
jcheck "report omits context when not a dry run" "${WORK}/occ.json.out" 'all(.[]; has("context") | not)'
"${RAILMARK[@]}" "${WORK}/occ.pdf" -o "${WORK}/occ.md" > /dev/null 2>&1
check   "highlight landed on the second occurrence" "${WORK}/occ.md" "A second **run of the experiment** repeated"
nocheck "first occurrence not bolded"               "${WORK}/occ.md" "every **run of the experiment**"

# --- 6. Suggested replacement ------------------------------------------------
# PDFium can't create the caret Acrobat pairs with a strikeout, so a replacement
# is a strikeout tagged via /Subj. That tag has to survive a real write + read.
echo ""
section "replace"
cat > "${WORK}/replace.json" <<'PLAN'
{"entries":[
 {"page":2,"quote":"small but consistent","type":"replace","replacement":"modest but reliable","comment":"Plainer wording."},
 {"page":2,"quote":"No attempt","type":"strikeout"}
]}
PLAN
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/replace.json" -o "${WORK}/replace.pdf" > "${WORK}/replace.out" 2>/dev/null
jcheck "replace entry resolved"   "${WORK}/replace.out" '.[0].success and .[0].type == "Replace"'
"${RAILMARK[@]}" "${WORK}/replace.pdf" -o "${WORK}/replace.md" > /dev/null 2>&1
check  "replacement rendered"      "${WORK}/replace.md" "~~small but consistent~~ → **modest but reliable**"
check  "replacement comment"       "${WORK}/replace.md" "**Comment:** Plainer wording."
check  "replacement labelled"      "${WORK}/replace.md" "suggested replacement"
check  "plain strikeout unchanged" "${WORK}/replace.md" "~~No attempt~~"
nocheck "tag not leaked"           "${WORK}/replace.md" "Replace with:"

# --- 7. Re-applying a plan ----------------------------------------------------
# Geometry read back from a PDF is not bit-identical to what was written, so
# only a real round trip shows that re-applying a plan finds its own markup.
echo ""
section "idempotent apply"
cat > "${WORK}/idem.json" <<'PLAN'
{"entries":[
 {"page":1,"quote":"interpretable in practice","type":"highlight","comment":"first round"},
 {"page":1,"quote":"best available option","type":"underline"},
 {"page":1,"quote":"results were averaged","type":"squiggly"},
 {"page":1,"quote":"paragraph sits under","type":"strikeout"},
 {"page":1,"quote":"Each sample was measured twice","type":"note","comment":"How many samples?"},
 {"page":2,"quote":"small but consistent","type":"replace","replacement":"modest"}
]}
PLAN
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/idem.json" -o "${WORK}/idem1.pdf" > "${WORK}/idem1.out" 2>/dev/null
jcheck "first apply adds everything" "${WORK}/idem1.out" 'all(.[]; .action == "added")'

"${RAILMARK[@]}" "${WORK}/idem1.pdf" --apply-markup "${WORK}/idem.json" -o "${WORK}/idem2.pdf" > "${WORK}/idem2.out" 2>/dev/null
jcheck "second apply finds every annotation" "${WORK}/idem2.out" 'all(.[]; .action == "unchanged")'
"${RAILMARK[@]}" "${WORK}/idem2.pdf" -o "${WORK}/idem2.md" > /dev/null 2>&1
check "no duplicates after re-apply" "${WORK}/idem2.md" "**10 annotations**"

# Revising one comment updates that annotation instead of adding a second.
sed 's/first round/second round/' "${WORK}/idem.json" > "${WORK}/idem-rev.json"
"${RAILMARK[@]}" "${WORK}/idem2.pdf" --apply-markup "${WORK}/idem-rev.json" -o "${WORK}/idem3.pdf" > "${WORK}/idem3.out" 2>/dev/null
jcheck "revised comment updated in place" "${WORK}/idem3.out" '.[0].action == "updated" and ([.[1:][] | .action] | all(. == "unchanged"))'
"${RAILMARK[@]}" "${WORK}/idem3.pdf" -o "${WORK}/idem3.md" > /dev/null 2>&1
check   "still no duplicates"    "${WORK}/idem3.md" "**10 annotations**"
check   "new comment present"    "${WORK}/idem3.md" "**Comment:** second round"
nocheck "old comment gone"       "${WORK}/idem3.md" "first round"

# --replace-author clears an earlier round, leaving the fixture's own
# annotations (author "fixture") alone.
cat > "${WORK}/round2.json" <<'PLAN'
{"entries":[{"page":2,"quote":"No attempt was made","type":"highlight","comment":"round two"}]}
PLAN
"${RAILMARK[@]}" "${WORK}/idem3.pdf" --apply-markup "${WORK}/round2.json" --replace-author "AI Reviewer" \
    -o "${WORK}/round2.pdf" > /dev/null 2> "${WORK}/round2.err"
check "removal reported" "${WORK}/round2.err" 'Removed 6 earlier annotation(s) by "AI Reviewer"'
"${RAILMARK[@]}" "${WORK}/round2.pdf" -o "${WORK}/round2.md" > /dev/null 2>&1
check   "only the new round and the fixture's own remain" "${WORK}/round2.md" "**5 annotations**"
check   "new round present"  "${WORK}/round2.md" "round two"
nocheck "old round gone"     "${WORK}/round2.md" "second round"

echo ""
if [ "$failures" -ne 0 ]; then
    echo "${failures} check(s) failed." >&2
    exit 1
fi
echo "All integration checks passed."
