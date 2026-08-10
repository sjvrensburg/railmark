# Markup plan JSON schema

A markup plan is a single JSON object passed to `railmark <pdf> --apply-markup <plan.json>`.

```json
{
  "entries": [
    { "page": 3, "quote": "...", "type": "highlight", "comment": "...", "color": "#FFFF00", "author": "AI Reviewer" }
  ]
}
```

## Top level

| Field     | Type            | Required | Notes                          |
|-----------|-----------------|----------|---------------------------------|
| `entries` | array of Entry  | yes      | One object per markup action.  |

## Entry fields

| Field     | Type   | Required | Notes |
|-----------|--------|----------|-------|
| `page`    | int    | yes      | **1-based physical PDF page index** — the Nth page in the file, counting the cover as page 1 (this is what a PDF reader shows in its total-page count, and what `railmark --export`'s page breaks are numbered with). It is **not** the PDF's internal 0-based page index, and it is **not** the printed page label — a document with roman-numeral front matter (`i, ii, iii, …`) followed by an Arabic restart at Chapter 1 has printed labels that don't match physical position at all, and can't be expressed as an integer for the front matter. Always get `page` by counting from `--export`'s output, never from a page number printed on the page itself. |
| `quote`   | string | yes      | Exact, verbatim, contiguous substring of the PDF's extracted text on that page. See "Quote rules" below. |
| `type`    | string | yes      | One of: `highlight`, `underline`, `strikeout`, `squiggly`, `note` (case-insensitive). |
| `comment` | string | no       | For `highlight`/`underline`/`strikeout`/`squiggly`, becomes the annotation's `Contents` (the popup comment). For `note`, becomes the note body text itself — a `note` entry with no comment produces an empty sticky note, which is rarely useful. `note` is placed as a margin sticky note pinned to the right edge of the page (24pt inset) at the vertical midpoint of the matched quote — it does **not** attach inline to the quoted text the way the other types do. |
| `color`   | string | no       | Hex color (`#RRGGBB`). If omitted, a type-specific default is used: highlight `#FFFF00`, underline `#00AAFF`, strikeout `#FF0000`, squiggly `#FF8800`, note `#FFCC00`. |
| `author`  | string | no       | Defaults to `"AI Reviewer"` if omitted. |

## Output

`--apply-markup` never touches the input PDF by default. It writes the marked-up document to a
new file — `-o <path>`, or `<pdf-stem>-marked.pdf` if `-o` is omitted. Pass `--in-place` to write
annotations directly into the input PDF instead (not recommended for the workflow in this skill —
prefer letting each apply produce a fresh `-marked.pdf` so a botched or repeated run can't
accumulate duplicate annotations in the source document). `-o -` (stdout) is not supported for
`--apply-markup` since the output is a binary PDF, not text.

## Quote rules

The `quote` field is the only link between your editorial judgment and the PDF's physical geometry — RailMark resolves it against the page's extracted text to find where to draw the markup. Rules:

- Must be an **exact substring** of the page's text (matching is case-insensitive and tolerant of whitespace differences — extra/collapsed spaces, tabs, line-wraps — but **not** of paraphrasing, reordering, or substituted punctuation).
- Must be **contiguous** — it cannot span a page break, and for best results keep it to one sentence or phrase rather than a whole paragraph (long quotes are more likely to hit a formatting difference somewhere in the middle).
- Must come from **prose**, not the `--export` Markdown's own formatting — don't include `**bold**` markers, heading `#` characters, or table pipe characters as part of the quote; those are Markdown, not PDF text.
- Type of markup that spans a single word (e.g. `underline` on a term) resolves most reliably. Long strikeouts across multiple sentences are more failure-prone.

## Result report

`railmark --apply-markup` prints a JSON array to **stdout**, one object per plan entry, in plan order:

```json
[
  { "page": 3, "quote": "the null hypothesis is rejected at p < 0.01", "type": "Highlight", "success": true,  "error": null, "spanCount": 1 },
  { "page": 5, "quote": "some paraphrased text", "type": "Note", "success": false, "error": "Quote not found on page 5.", "spanCount": 0 }
]
```

On success, `spanCount` is the number of visual-line quad spans the quote resolved to (1 for a quote that stays on one line, 2+ for a quote that wraps across a line break). Use it as a sanity check for multi-line quotes: a long quote that resolved `success: true` but reports `spanCount: 1` covered less text than expected — the match likely only landed on part of it. `spanCount` is `0` for failed entries.

A human-readable summary and per-failure lines are printed to **stderr**. Exit code `0` means every entry resolved (and was written, unless `--dry-run` was passed); exit code `2` means one or more entries failed to resolve — well-formed entries are still applied. Exit code `1` is a hard error (bad JSON, missing file, or a write failure) and nothing is written.

Run `railmark --version` to check the installed version when filing a bug report.
