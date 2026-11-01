#!/usr/bin/env bash
# docx-smoke.sh — exercise `railmark --from-docx` end to end: a generated Word review document is
# rendered by LibreOffice and its comments/tracked changes written as PDF annotations, which are
# then read back with railmark's own `--format json` extraction and checked.
#
# Usage:
#   ./tests/docx-smoke.sh <railmark command...>
#   ./tests/docx-smoke.sh dotnet run --project RailMark/ --
#
# Needs LibreOffice (soffice on PATH, or RAILMARK_SOFFICE), python3 and jq. Without LibreOffice
# it reports SKIP and exits 0 — unless RAILMARK_REQUIRE_SOFFICE is set (CI sets it), in which
# case a missing LibreOffice is a failure, so the checks can't silently stop running.

set -euo pipefail

if [ $# -eq 0 ]; then
    echo "Usage: $0 <railmark command...>" >&2
    exit 2
fi

if [ -z "${RAILMARK_SOFFICE:-}" ] && ! command -v soffice >/dev/null && ! command -v libreoffice >/dev/null; then
    if [ -n "${RAILMARK_REQUIRE_SOFFICE:-}" ]; then
        echo "FAIL docx-smoke: LibreOffice (soffice) not found and RAILMARK_REQUIRE_SOFFICE is set" >&2
        exit 1
    fi
    echo "SKIP docx-smoke: LibreOffice (soffice) not found"
    exit 0
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
# RAILMARK_SMOKE_OUT=<dir> keeps the generated .docx, the PDF and both JSON files (CI uploads them on failure).
keep_outputs() {
    if [ -n "${RAILMARK_SMOKE_OUT:-}" ]; then
        mkdir -p "$RAILMARK_SMOKE_OUT"
        cp "$WORK"/review.docx "$WORK"/review.pdf "$WORK"/report.json "$WORK"/annotations.json "$RAILMARK_SMOKE_OUT"/ 2>/dev/null || true
    fi
    rm -rf "$WORK"
}
trap keep_outputs EXIT
RAILMARK=("$@")
failures=0

expect() {
    local label="$1" filter="$2"
    if jq -e "$filter" "$WORK/annotations.json" >/dev/null; then
        echo "  ok   ${label}"
    else
        echo "  FAIL ${label} — jq: ${filter}" >&2
        failures=$((failures + 1))
    fi
}

python3 "$REPO_ROOT/tests/fixtures/make-review-docx.py" "$WORK/review.docx"

echo "== --from-docx"
set +e
"${RAILMARK[@]}" --from-docx "$WORK/review.docx" -o "$WORK/review.pdf" >"$WORK/report.json" 2>"$WORK/stderr.txt"
status=$?
set -e
cat "$WORK/stderr.txt"
if [ "$status" -ne 0 ]; then
    echo "  FAIL exit code ${status}" >&2
    exit 1
fi
echo "  ok   exit code 0"
if [ "$(jq '[.[] | select(.success | not)] | length' "$WORK/report.json")" = "0" ] \
   && [ "$(jq 'length' "$WORK/report.json")" = "15" ]; then
    echo "  ok   all 15 review items placed"
else
    echo "  FAIL report: $(cat "$WORK/report.json")" >&2
    failures=$((failures + 1))
fi

if [ ! -s "$WORK/review.pdf" ]; then
    echo "  FAIL no PDF was written; stopping" >&2
    exit 1
fi

# Read back with a throw-away HOME so no RailReader2 sidecar or document map is consulted.
mkdir -p "$WORK/home"
HOME="$WORK/home" "${RAILMARK[@]}" "$WORK/review.pdf" --format json -o "$WORK/annotations.json" 2>/dev/null
if ! jq -e '.annotations' "$WORK/annotations.json" >/dev/null 2>&1; then
    echo "  FAIL reading the annotations back produced no JSON; stopping" >&2
    exit 1
fi

echo "== read back"
A='.annotations[]'
expect "the original text is rendered (deleted 'three' is on the page, struck through as a replacement)" \
    "[$A | select(.type == \"replace\" and .quote == \"three\" and .replacement == \"four\" and .author == \"Carol\")] | length == 1"
expect "comment → highlight over exactly its range" \
    "[$A | select(.type == \"highlight\" and .quote == \"brown fox\" and .author == \"Alice\" and .comment == \"Is \\\"brown\\\" the right colour?\")] | length == 1"
expect "the reply is threaded under its comment" \
    "[$A | select(.type == \"note\" and .author == \"Bob\" and (.inReplyTo // \"\" | startswith(\"docx-comment-0-\")))] | length == 1"
expect "a repeated phrase: the comment lands on the occurrence it was made on" \
    "[$A | select(.type == \"highlight\" and .comment == \"Second occurrence.\" and .occurrence == 3)] | length == 1"
expect "a resolved comment carries the Completed state" \
    "[$A | select(.type == \"highlight\" and .quote == \"this was fixed\" and .state == \"completed\")] | length == 1"
expect "a comment with no range → note" \
    "[$A | select(.type == \"note\" and .author == \"Ivy\" and .comment == \"A point comment.\")] | length == 1"
expect "a comment inside a table cell" \
    "[$A | select(.type == \"highlight\" and .quote == \"B1 value\")] | length == 1"
expect "a comment over the page break is one markup spanning pp. 1–2, without header or footer text" \
    "[$A | select(.type == \"highlight\" and .author == \"Leo\" and .page == 1 and .endPage == 2
        and (.quote | startswith(\"a comment that runs\")) and (.quote | endswith(\"until here\"))
        and (.quote | test(\"RUNNING HEADER|Running footer\") | not))] | length == 1"
expect "deletion → strikeout" \
    "[$A | select(.type == \"strikeout\" and .quote == \"remove this phrase,\" and .author == \"Dave\")] | length == 1"
expect "deletion over the page break is one strikeout spanning pp. 2–3" \
    "[$A | select(.type == \"strikeout\" and .author == \"Hank\" and .page == 2 and .endPage == 3
        and (.quote | startswith(\"a deletion that also runs\")) and (.quote | endswith(\"over the break\"))
        and (.quote | test(\"RUNNING HEADER|Running footer\") | not))] | length == 1"
expect "insertions → notes carrying the inserted text" \
    "([$A | select(.type == \"note\" and .comment == \"Insert: “This sentence was inserted.”\")] | length == 1)
     and ([$A | select(.type == \"note\" and .comment == \"Insert: “A whole inserted paragraph.”\")] | length == 1)"
expect "format change → underline describing it" \
    "[$A | select(.type == \"underline\" and .quote == \"important\" and .comment == \"Formatted: Bold\")] | length == 1"
expect "move → strikeout at the source and a note at the destination" \
    "([$A | select(.type == \"strikeout\" and .quote == \"Moved sentence.\")] | length == 1)
     and ([$A | select(.type == \"note\" and .comment == \"Moved here: “Moved sentence.”\")] | length == 1)"
expect "Word's dates survive" \
    "[$A | select(.author == \"Bob\" and (.created // .date // \"\" | startswith(\"2026-10-31\")))] | length == 1"

if [ "$failures" -gt 0 ]; then
    # Enough to diagnose without the PDF: every record, quotes shortened, plus the placement report.
    echo "--- read-back records:" >&2
    jq -c '.annotations[] | del(.color, .opacity, .date, .created, .modified)' "$WORK/annotations.json" >&2
    echo "--- placement report:" >&2
    jq -c '.[]' "$WORK/report.json" >&2
    echo "${failures} check(s) failed." >&2
    exit 1
fi
echo "All docx checks passed."
