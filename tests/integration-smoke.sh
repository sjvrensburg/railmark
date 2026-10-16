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

# --- 8. JSON extraction --------------------------------------------------------
echo ""
section "--format json"
"${RAILMARK[@]}" "${WORK}/idem1.pdf" --format json -o - > "${WORK}/idem1.ann.json" 2>/dev/null
J="${WORK}/idem1.ann.json"
jcheck "every annotation extracted"      "$J" '.annotations | length == 10'
jcheck "source named"                    "$J" '.source == "idem1.pdf"'
jcheck "authored markup has ids"         "$J" '[.annotations[] | select(.author == "AI Reviewer") | .id] | all(type == "string" and length > 0)'
jcheck "highlight quote across wrap"     "$J" 'any(.annotations[]; .type == "highlight" and .quote == "interpretable in practice" and .comment == "first round")'
jcheck "note body as comment"            "$J" 'any(.annotations[]; .type == "note" and .comment == "How many samples?")'
jcheck "replace record"                  "$J" 'any(.annotations[]; .type == "replace" and .replacement == "modest" and .quote == "small but consistent")'
jcheck "heading from position"           "$J" 'any(.annotations[]; .quote == "paragraph sits under" and .heading == "Methods")'
jcheck "1-based pages"                   "$J" '[.annotations[].page] | min == 1 and max == 2'
jcheck "reading order"                   "$J" '[.annotations[].page] | . == sort'

# Records are shaped like plan entries: turn the extracted markup straight back
# into a plan, and re-applying it must find every annotation already there.
jq '{entries: [.annotations[] | select(.quote != null) | {page, quote, type, comment, replacement, color, author, occurrence}]}' \
    "$J" > "${WORK}/roundtrip.json"
"${RAILMARK[@]}" "${WORK}/idem1.pdf" --apply-markup "${WORK}/roundtrip.json" --dry-run > "${WORK}/roundtrip.out" 2>/dev/null
jcheck "extracted records re-apply as a plan" "${WORK}/roundtrip.out" 'length == 5 and all(.[]; .success and .action == "unchanged")'

# The highlight in occ.pdf sits on the second of two matches; the record must
# say so, and feeding it back must hit that same match.
"${RAILMARK[@]}" "${WORK}/occ.pdf" --format json -o - > "${WORK}/occ.ann.json" 2>/dev/null
jcheck "occurrence recorded for a repeated quote" "${WORK}/occ.ann.json" 'any(.annotations[]; .type == "highlight" and .occurrence == 2)'
jq '{entries: [.annotations[] | select(.type == "highlight") | {page, quote, type, author, occurrence}]}' \
    "${WORK}/occ.ann.json" > "${WORK}/occ-rt.json"
"${RAILMARK[@]}" "${WORK}/occ.pdf" --apply-markup "${WORK}/occ-rt.json" --dry-run > "${WORK}/occ-rt.out" 2>/dev/null
jcheck "occurrence round-trips to the same match" "${WORK}/occ-rt.out" '.[0].action == "unchanged"'

# An empty result is still valid JSON.
"${RAILMARK[@]}" "$FIXTURE" --format json --pages 3 -o - > "${WORK}/empty.json" 2>/dev/null
jcheck "empty result is an empty array" "${WORK}/empty.json" '.annotations == []'

if "${RAILMARK[@]}" "$FIXTURE" --format json --export -o - > /dev/null 2>&1; then
    echo "  FAIL --format json --export should be rejected" >&2
    failures=$((failures + 1))
else
    echo "  ok   --format json rejected with --export"
fi

# --- 9. Across a page break ------------------------------------------------------
# Page 2 ends mid-sentence, then a "Page 2" footer; the sentence finishes at the
# top of page 3. A quote of it must be split over the break, footer skipped.
echo ""
section "quote across a page break"
cat > "${WORK}/span.json" <<'PLAN'
{"entries":[
 {"page":2,"quote":"repeated the whole measurement with a fresh batch","type":"highlight","comment":"spans pages"},
 {"page":2,"quote":"the whole measurement","type":"replace","replacement":"each measurement"}
]}
PLAN
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/span.json" --dry-run > "${WORK}/span-dry.out" 2>/dev/null
jcheck "split over the break"          "${WORK}/span-dry.out" 'all(.[]; .success and .endPage == 3 and .spanCount == 2)'
jcheck "footer not part of the match"  "${WORK}/span-dry.out" '.[0].context | contains("[[repeated the whole]]") and contains("page 3: [[measurement with a fresh batch]]")'

"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/span.json" -o "${WORK}/span.pdf" > /dev/null 2>&1
"${RAILMARK[@]}" "${WORK}/span.pdf" -o "${WORK}/span.md" > /dev/null 2>&1
check "one highlight across both pages" "${WORK}/span.md" "**repeated the whole measurement with a fresh batch**"
check "labelled with both pages"        "${WORK}/span.md" "*(pp. 2–3, highlight)*"
check "one replacement across both"     "${WORK}/span.md" "~~the whole measurement~~ → **each measurement**"
nocheck "continuation not a deletion"   "${WORK}/span.md" "suggested deletion"

"${RAILMARK[@]}" "${WORK}/span.pdf" --format json -o - > "${WORK}/span.ann.json" 2>/dev/null
jcheck "JSON reports it once, with endPage" "${WORK}/span.ann.json" \
    '[.annotations[] | select(.author == "AI Reviewer")] | length == 2 and all(.[]; .page == 2 and .endPage == 3)'
jq '{entries: [.annotations[] | select(.author == "AI Reviewer") | {page, quote, type, comment, replacement}]}' \
    "${WORK}/span.ann.json" > "${WORK}/span-rt.json"
"${RAILMARK[@]}" "${WORK}/span.pdf" --apply-markup "${WORK}/span-rt.json" --dry-run > "${WORK}/span-rt.out" 2>/dev/null
jcheck "spanning records re-apply unchanged" "${WORK}/span-rt.out" 'length == 2 and all(.[]; .action == "unchanged")'

# A quote with the wrong page number, or misquoted, must fail — not "resolve"
# as a stray letter at the end of one page plus the rest at the top of the next.
cat > "${WORK}/nospan.json" <<'PLAN'
{"entries":[
 {"page":1,"quote":"No attempt was made","type":"underline"},
 {"page":2,"quote":"measurement with a fresh batch","type":"highlight"},
 {"page":2,"quote":"the samples, and it agreed","type":"highlight"}
]}
PLAN
set +e
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/nospan.json" --dry-run > "${WORK}/nospan.out" 2>/dev/null
set -e
jcheck "wrong-page and misquoted quotes fail" "${WORK}/nospan.out" 'all(.[]; .success | not)'
jcheck "wrong page named"                     "${WORK}/nospan.out" '.[0].error == "Quote not found on page 1 (it occurs on page 2)." and (.[1].error | contains("occurs on page 3"))'

# Two notes on different phrases of one line share a margin spot; both must survive.
cat > "${WORK}/twonotes.json" <<'PLAN'
{"entries":[
 {"page":1,"quote":"RailMark test suite","type":"note","comment":"First issue."},
 {"page":1,"quote":"PDF whose exact","type":"note","comment":"Second issue."}
]}
PLAN
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/twonotes.json" -o "${WORK}/twonotes.pdf" > "${WORK}/twonotes.out" 2>/dev/null
jcheck "both notes added" "${WORK}/twonotes.out" 'all(.[]; .action == "added")'
"${RAILMARK[@]}" "${WORK}/twonotes.pdf" --format json -o - > "${WORK}/twonotes.ann.json" 2>/dev/null
jcheck "both notes in the PDF" "${WORK}/twonotes.ann.json" '[.annotations[] | select(.type == "note") | .comment] | sort == ["First issue.", "Second issue."]'
"${RAILMARK[@]}" "${WORK}/twonotes.pdf" --apply-markup "${WORK}/twonotes.json" --dry-run > "${WORK}/twonotes2.out" 2>/dev/null
jcheck "both notes found on re-apply" "${WORK}/twonotes2.out" 'all(.[]; .action == "unchanged")'
# PDFium writes each note as a 24pt square /Rect; the raw PDF is the only place to read it back.
# Both icons sit just right of the text column (x≈452 on the fixture), not at the page edge, and
# the second is pushed below the first rather than onto it.
grep -ao '/Rect\[[^]]*\]/Subtype/Text' "${WORK}/twonotes.pdf" | sed 's#/Rect\[ *##; s#\]/Subtype/Text##' > "${WORK}/twonotes.rects"
if [ "$(wc -l < "${WORK}/twonotes.rects")" = 2 ] \
   && awk '{ if ($1 < 452 || $1 > 470) exit 1 }' "${WORK}/twonotes.rects" \
   && awk 'NR == 1 { x = $1; y = $2 } NR == 2 { dx = $1 - x; if (dx < -1 || dx > 1) exit 1; d = y - $2; if (d < 0) d = -d; if (d < 24) exit 1 }' "${WORK}/twonotes.rects"; then
    echo "  ok   notes sit beside the text column, stacked apart"
else
    echo "  FAIL notes sit beside the text column, stacked apart — rects: $(tr '\n' ';' < "${WORK}/twonotes.rects")" >&2
    failures=$((failures + 1))
fi

# --- 10. Replies -----------------------------------------------------------------
# PDFium can't write /IRT, so a reply's link to its parent rides in its /NM.
# Only a real write + read shows the link survives.
echo ""
section "replies"
cat > "${WORK}/human.json" <<'PLAN'
{"entries":[{"page":1,"quote":"results were averaged","type":"highlight","comment":"Mean or median?","author":"Human"}]}
PLAN
"${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/human.json" -o "${WORK}/human.pdf" > /dev/null 2>&1
"${RAILMARK[@]}" "${WORK}/human.pdf" --format json -o - > "${WORK}/human.ann.json" 2>/dev/null
parent_id=$(jq -r '.annotations[] | select(.author == "Human") | .id' "${WORK}/human.ann.json")
if [ -n "$parent_id" ] && [ "$parent_id" != "null" ]; then
    echo "  ok   human annotation has an id"
else
    echo "  FAIL human annotation has no id to reply to" >&2
    failures=$((failures + 1))
fi

jq -n --arg id "$parent_id" '{entries: [
  {page: 1, type: "note", inReplyTo: $id, comment: "The mean, per the methods."},
  {page: 1, type: "note", inReplyTo: $id, comment: "Consider reporting both."}
]}' > "${WORK}/reply.json"
"${RAILMARK[@]}" "${WORK}/human.pdf" --apply-markup "${WORK}/reply.json" -o "${WORK}/replied.pdf" > "${WORK}/reply.out" 2>/dev/null
jcheck "replies applied" "${WORK}/reply.out" 'length == 2 and all(.[]; .success and .action == "added" and .inReplyTo != null)'

"${RAILMARK[@]}" "${WORK}/replied.pdf" --format json -o - > "${WORK}/replied.ann.json" 2>/dev/null
jq -e --arg id "$parent_id" '[.annotations[] | select(.inReplyTo == $id and .type == "note")] | length == 2' \
    "${WORK}/replied.ann.json" > /dev/null \
    && echo "  ok   reply link survives the PDF" \
    || { echo "  FAIL reply link lost in the PDF" >&2; failures=$((failures + 1)); }

"${RAILMARK[@]}" "${WORK}/replied.pdf" -o "${WORK}/replied.md" > /dev/null 2>&1
check "reply threaded under its parent" "${WORK}/replied.md" "> > **Reply — AI Reviewer:** The mean, per the methods."
check "second reply threaded too"       "${WORK}/replied.md" "> > **Reply — AI Reviewer:** Consider reporting both."
if awk '/Mean or median\?/{p=1} p && /The mean, per the methods/{found=1} END{exit !found}' "${WORK}/replied.md"; then
    echo "  ok   replies follow their parent"
else
    echo "  FAIL replies do not follow their parent" >&2
    failures=$((failures + 1))
fi

"${RAILMARK[@]}" "${WORK}/replied.pdf" --apply-markup "${WORK}/reply.json" --dry-run > "${WORK}/reply2.out" 2>/dev/null
jcheck "re-applying replies is unchanged" "${WORK}/reply2.out" 'all(.[]; .action == "unchanged")'

# A thread stays contiguous: replying to the first of the two stacked notes from section 9 puts
# the reply right under it and pushes the second note down, so the icons read parent, reply,
# sibling from the top of the page. (PDF /Rect is bottom-up: a higher y is higher on the page.)
first_id=$(jq -r '.annotations[] | select(.comment == "First issue.") | .id' "${WORK}/twonotes.ann.json")
jq -n --arg id "$first_id" '{entries: [{page: 1, type: "note", inReplyTo: $id, comment: "Reply to the first."}]}' > "${WORK}/thread.json"
"${RAILMARK[@]}" "${WORK}/twonotes.pdf" --apply-markup "${WORK}/thread.json" -o "${WORK}/thread.pdf" > /dev/null 2>&1
python_free_order=$(grep -ao '/Contents([^)]*)[^>]*/Rect\[[^]]*\]/Subtype/Text' "${WORK}/thread.pdf" \
    | sed 's#/Contents(\([^)]*\)).*/Rect\[ *\([^ ]*\) *\([^ ]*\).*#\3 \1#' | sort -rn | cut -d' ' -f2- | paste -sd '|')
if [ "$python_free_order" = "First issue.|Reply to the first.|Second issue." ]; then
    echo "  ok   reply sits under its parent, sibling pushed down"
else
    echo "  FAIL reply sits under its parent, sibling pushed down — got: ${python_free_order}" >&2
    failures=$((failures + 1))
fi

# --- 11. Stdin plans and extraction filters -------------------------------------
echo ""
section "stdin plan and filters"
"${RAILMARK[@]}" "$FIXTURE" --apply-markup - --dry-run < "${WORK}/plan.json" > "${WORK}/stdin.out" 2>/dev/null
jcheck "plan read from stdin" "${WORK}/stdin.out" 'length == 4 and all(.[]; .success)'

# replied.pdf holds the fixture's own 4 (author "fixture", undated), one
# "Human" highlight and two "AI Reviewer" replies.
R="${WORK}/replied.pdf"
"${RAILMARK[@]}" "$R" --format json --author human -o - > "${WORK}/f1.json" 2>/dev/null
jcheck "--author keeps only that author" "${WORK}/f1.json" '[.annotations[].author] == ["Human"]'
"${RAILMARK[@]}" "$R" --format json --exclude-author "AI Reviewer" -o - > "${WORK}/f2.json" 2>/dev/null
jcheck "--exclude-author drops the AI markup" "${WORK}/f2.json" \
    '(.annotations | length == 5) and all(.annotations[]; .author != "AI Reviewer")'
"${RAILMARK[@]}" "$R" --format json --since 2000-01-01 -o - > "${WORK}/f3.json" 2> "${WORK}/f3.err"
jcheck "--since keeps dated annotations" "${WORK}/f3.json" '(.annotations | length == 3) and all(.annotations[]; .modified != null)'
check  "--since reports undated exclusions" "${WORK}/f3.err" "excluded 4 annotation(s) that carry no date"
"${RAILMARK[@]}" "$R" --format json --since 2999-01-01 -o - > "${WORK}/f4.json" 2>/dev/null
jcheck "--since in the future leaves nothing" "${WORK}/f4.json" '.annotations == []'
"${RAILMARK[@]}" "$R" --author human -o "${WORK}/f5.md" > /dev/null 2>&1
check "filters apply to Markdown too" "${WORK}/f5.md" "**1 annotations**"

if "${RAILMARK[@]}" "$R" --since yesterday-ish -o - > /dev/null 2>&1; then
    echo "  FAIL an unparseable --since date should be rejected" >&2
    failures=$((failures + 1))
else
    echo "  ok   unparseable --since rejected"
fi

# --- 11. Unauthored annotations, remove by id, update by id -------------------
# Viewers often write annotations with no /T (author). Strip it from a markup
# round with qpdf to reproduce that; without qpdf the section is skipped.
echo ""
section "unauthored annotations, --remove-ids, update by id"
if ! command -v qpdf > /dev/null 2>&1; then
    echo "  skip qpdf not installed"
else
    cat > "${WORK}/un.json" <<'PLAN'
{"entries":[
 {"page":1,"quote":"Introduction","type":"highlight","comment":"c","author":"AI Reviewer"},
 {"page":2,"quote":"No attempt was made","type":"note","comment":"n","author":"AI Reviewer"}]}
PLAN
    "${RAILMARK[@]}" "$FIXTURE" --apply-markup "${WORK}/un.json" -o "${WORK}/un1.pdf" > /dev/null 2>&1
    qpdf --qdf --object-streams=disable "${WORK}/un1.pdf" "${WORK}/un1.qdf"
    sed 's#/T (AI Reviewer)##' "${WORK}/un1.qdf" > "${WORK}/un2.qdf"
    qpdf "${WORK}/un2.qdf" "${WORK}/un2.pdf" 2> /dev/null || [ $? -eq 3 ]  # 3 = repaired xref, expected after the sed
    "${RAILMARK[@]}" "${WORK}/un2.pdf" --format json -o - > "${WORK}/un2.json" 2>/dev/null
    jcheck "markup now has no author" "${WORK}/un2.json" '[.annotations[] | select(.type == "highlight" or .type == "note") | .author] == [null, null]'
    HL="$(jq -r '.annotations[] | select(.type == "highlight") | .id' "${WORK}/un2.json")"
    NOTE="$(jq -r '.annotations[] | select(.type == "note") | .id' "${WORK}/un2.json")"
    CREATED="$(jq -r '.annotations[] | select(.type == "highlight") | .created' "${WORK}/un2.json")"

    # Update by id: no quote, no geometry; id and creation date survive.
    cat > "${WORK}/upd.json" <<PLAN
{"entries":[
 {"page":1,"update":"${HL}","comment":"expanded","color":"#7FBFFF","author":"Stefan"},
 {"page":2,"update":"${NOTE}","comment":"longer note"},
 {"page":1,"update":"no-such-id","comment":"x"}]}
PLAN
    "${RAILMARK[@]}" "${WORK}/un2.pdf" --apply-markup "${WORK}/upd.json" -o "${WORK}/upd.pdf" \
        > "${WORK}/upd.out" 2> /dev/null || [ $? -eq 2 ]
    jcheck "update reports updated"      "${WORK}/upd.out" '.[0].action == "updated" and .[1].action == "updated"'
    jcheck "unknown id fails the entry"  "${WORK}/upd.out" '.[2].success == false'
    "${RAILMARK[@]}" "${WORK}/upd.pdf" --format json -o - > "${WORK}/upd.ann.json" 2>/dev/null
    jcheck "highlight changed in place"  "${WORK}/upd.ann.json" \
        ".annotations[] | select(.id == \"${HL}\") | .comment == \"expanded\" and .author == \"Stefan\" and .color == \"#7FBFFF\" and .created == \"${CREATED}\""
    jcheck "note rewritten, not duplicated" "${WORK}/upd.ann.json" \
        '([.annotations[] | select(.type == "note")] | length == 1) and ([.annotations[] | select(.type == "note")][0].comment == "longer note")'
    jcheck "nothing else added"          "${WORK}/upd.ann.json" '.annotations | length == 6'

    # --replace-unauthored / --replace-author "" / --remove-ids
    echo '{"entries":[]}' > "${WORK}/empty.json"
    "${RAILMARK[@]}" "${WORK}/un2.pdf" --apply-markup "${WORK}/empty.json" --replace-unauthored \
        -o "${WORK}/rm1.pdf" > /dev/null 2> "${WORK}/rm1.err"
    check "unauthored removal reported" "${WORK}/rm1.err" "Removed 2 earlier annotation(s) with no author"
    "${RAILMARK[@]}" "${WORK}/rm1.pdf" --format json -o - > "${WORK}/rm1.json" 2>/dev/null
    jcheck "only authored annotations remain" "${WORK}/rm1.json" '(.annotations | length == 4) and all(.annotations[]; .author == "fixture")'
    "${RAILMARK[@]}" "${WORK}/un2.pdf" --apply-markup "${WORK}/empty.json" --replace-author "" \
        -o "${WORK}/rm2.pdf" > /dev/null 2> "${WORK}/rm2.err"
    check "--replace-author \"\" means unauthored" "${WORK}/rm2.err" "Removed 2 earlier annotation(s) with no author"
    "${RAILMARK[@]}" "${WORK}/un2.pdf" --apply-markup "${WORK}/empty.json" --remove-ids "${HL},no-such-id" \
        -o "${WORK}/rm3.pdf" > /dev/null 2> "${WORK}/rm3.err"
    check "id removal reported"       "${WORK}/rm3.err" "Removed 1 earlier annotation(s) named by --remove-ids"
    check "unknown id warned"         "${WORK}/rm3.err" "no annotation with id no-such-id"
    "${RAILMARK[@]}" "${WORK}/rm3.pdf" --format json -o - > "${WORK}/rm3.json" 2>/dev/null
    jcheck "only the named annotation removed" "${WORK}/rm3.json" '.annotations | length == 5'
fi

echo ""
if [ "$failures" -ne 0 ]; then
    echo "${failures} check(s) failed." >&2
    exit 1
fi
echo "All integration checks passed."
