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
| `page`    | int    | yes      | **1-based** page number, matching what a human/CLI would call "page N". Not the PDF's internal 0-based page index. |
| `quote`   | string | yes      | Exact, verbatim, contiguous substring of the PDF's extracted text on that page. See "Quote rules" below. |
| `type`    | string | yes      | One of: `highlight`, `underline`, `strikeout`, `squiggly`, `note` (case-insensitive). |
| `comment` | string | no       | For `highlight`/`underline`/`strikeout`/`squiggly`, becomes the annotation's `Contents` (the popup comment). For `note`, becomes the note body text itself — a `note` entry with no comment produces an empty sticky note, which is rarely useful. |
| `color`   | string | no       | Hex color (`#RRGGBB`). If omitted, a type-specific default is used: highlight `#FFFF00`, underline `#00AAFF`, strikeout `#FF0000`, squiggly `#FF8800`, note `#FFCC00`. |
| `author`  | string | no       | Defaults to `"AI Reviewer"` if omitted. |

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
  { "page": 3, "quote": "the null hypothesis is rejected at p < 0.01", "type": "Highlight", "success": true,  "error": null },
  { "page": 5, "quote": "some paraphrased text", "type": "Note", "success": false, "error": "Quote not found on page 5." }
]
```

A human-readable summary and per-failure lines are printed to **stderr**. Exit code `0` means every entry resolved (and was written, unless `--dry-run` was passed); exit code `2` means one or more entries failed to resolve — well-formed entries are still applied. Exit code `1` is a hard error (bad JSON, missing file, or a write failure) and nothing is written.
