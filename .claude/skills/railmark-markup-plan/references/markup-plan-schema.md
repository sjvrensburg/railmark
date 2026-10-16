# Markup plan JSON schema

A markup plan is a single JSON object passed to `railmark <pdf> --apply-markup <plan.json>`
(or `--apply-markup -` to read it from stdin).

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
| `page`    | int    | yes      | **1-based physical PDF page index** — the Nth page in the file, counting the cover as page 1 (this is what a PDF reader shows in its total-page count, and what `railmark --export`'s page breaks are numbered with). It is **not** the PDF's internal 0-based page index, and it is **not** the printed page label — a document with roman-numeral front matter (`i, ii, iii, …`) followed by an Arabic restart at Chapter 1 has printed labels that don't match physical position at all, and can't be expressed as an integer for the front matter. Always get `page` by counting from `--export`'s output, never from a page number printed on the page itself. For a quote that runs over a page break, `page` is the page it **starts** on. |
| `quote`   | string | yes, except for a reply note | Exact, verbatim, contiguous substring of the PDF's extracted text on that page. See "Quote rules" below. For the text-markup types it is the text that gets marked. For a `note` it only says where the icon goes — beside the quoted line — so quote the opening words of the paragraph the note is about. May be omitted only by a `note` with `inReplyTo` — see "Replies". |
| `type`    | string | yes, except for `update` | One of: `highlight`, `underline`, `strikeout`, `squiggly`, `replace`, `note` (case-insensitive). |
| `comment` | string | no       | For `highlight`/`underline`/`strikeout`/`squiggly`/`replace`, becomes the annotation's popup comment. For `note`, becomes the note body text itself — a `note` entry with no comment produces an empty sticky note, which is rarely useful. `note` is placed as a sticky-note icon just outside the text column, level with the quoted line (in the nearer margin, or the gutter of a two-column page), and below any note already there — it does **not** attach to the quoted text the way the other types do, which is why a note is for a remark about a passage and a highlight with a `comment` for a remark about particular words (see the skill's editorial conventions). |
| `replacement` | string | for `replace` | The text that should replace the quote. Required (non-empty, and without a blank line in it) for `replace`, and rejected on every other type. To suggest deleting text outright, use `strikeout`. |
| `occurrence` | int | no | When the quote occurs more than once on the page: which match to mark, 1-based in reading order (counted among the matches that also satisfy `contextBefore`/`contextAfter`, if given). See "Repeated quotes". |
| `contextBefore` | string | no | Text that must immediately precede the quote, to pick one of several matches. |
| `contextAfter`  | string | no | Text that must immediately follow the quote, to pick one of several matches. |
| `inReplyTo` | string | no | The `id` of an existing annotation on the same page, as reported by `railmark <pdf> --format json`. Makes this entry a reply to that annotation. See "Replies". |
| `update` | string | no | The `id` of an existing annotation on `page`, as reported by `--format json`. Changes that annotation in place instead of adding one — see "Updating an existing annotation". |
| `color`   | string | no       | Hex color (`#RRGGBB`). If omitted, a type-specific default from RailReader2's annotation palette is used: highlight `#FFFF00`, underline `#00A000`, strikeout `#FF0000`, squiggly `#00A000`, note `#FFFF00`, replace `#0066FF`. The full palette is yellow `#FFFF00`, green `#00A000`, red `#FF0000`, blue `#0066FF`, black `#000000` — prefer these when overriding, so the colours can be reproduced in RailReader2. |
| `opacity` | number | no | Opacity of a text-markup annotation, above 0 and at most 1 (`/CA` in the PDF); ignored for a `note`. Falls back to `--opacity`, then to **0.35 for a `highlight`** (the value RailReader2's own Highlight tool uses, which keeps text readable under any colour from the palette) and 1 for the other types. Setting it is what makes a re-applied entry change an existing annotation's opacity; leaving it unset never repaints one already in the PDF. Also valid on an `update` entry, for an annotation of any type. |
| `author`  | string | no       | Defaults to `"AI Reviewer"` if omitted. |

## Output

`--apply-markup` never touches the input PDF by default. It writes the marked-up document to a
new file — `-o <path>`, or `<pdf-stem>-marked.pdf` if `-o` is omitted. Pass `--in-place` to write
annotations directly into the input PDF instead. `-o -` (stdout) is not supported for
`--apply-markup` since the output is a binary PDF, not text.

## Re-applying a plan

Applying is **idempotent**. Before adding an annotation, RailMark looks for one already covering
the same text — same type, same author, same geometry — and:

- if it is identical, leaves it alone (`"action": "unchanged"`);
- if only its `comment` or `color` differs, updates it in place (`"action": "updated"`);
- otherwise adds a new one (`"action": "added"`).

A `note` is the exception: a note is a free-standing icon beside its line, so what it says is
part of which note it is — several notes on one line are told apart by their text. A note whose
text you changed is therefore **added** alongside the old one — use `--replace-author` (below) to
drop the old round.

Two entries in **one** plan that put different markup on the same text — the same highlight with
two different comments, two different replacements for the same words — contradict each other.
The first is applied; the second fails with `Conflicts with entry N of this plan`. Merge them.

So re-running a whole plan after fixing a few failed quotes, or re-running a revised plan against
the previous `-marked.pdf`, does not stack duplicates. Two entries by **different** authors on the
same text are two annotations, deliberately.

To **revise** an earlier round of markup rather than build on it — drop entries you no longer
stand behind — pass `--replace-author "AI Reviewer"` (or whichever author the round used). Every
existing annotation by that author is removed first, then the plan is applied. The match is exact
and case-sensitive; other authors' annotations are never touched.

Annotations with **no author** (many viewers write none) are selected with `--replace-unauthored`
(or `--replace-author ""`). To remove particular annotations, pass their ids from `--format json`:
`--remove-ids <id,id,...>` (a markup that runs over a page break is removed with its tail; an id
that matches nothing is warned about). Replies to a removed annotation are **not** removed with it: they stay as ordinary top-level notes, and a later `inReplyTo` to the removed id fails. Remove them too (by id) if you want the thread gone.

## Updating an existing annotation

`update` changes an annotation that already exists — whoever made it, of any type including notes
and freehand — without looking up a quote or recomputing geometry:

```json
{ "page": 17, "update": "<id>", "comment": "Expanded comment text…", "color": "#7FBFFF", "author": "Stefan" }
```

- Only `comment`, `color`, `author` and `opacity` are applied, and at least one is required. Omit a field to
  leave it alone; `"comment": ""` clears the comment.
- The annotation keeps its id, creation date and position. `quote`, `type`, `occurrence` and
  context are ignored; `inReplyTo` and `replacement` are rejected.
- A `replace` annotation keeps its `Replace with:` text; `comment` changes the comment after it.
- `page` must be the page the annotation is on. An unknown id fails the entry; two updates of one
  annotation in a plan conflict, as in "Re-applying a plan".
- Only annotations that carry an id can be targeted: a PDF annotation with no `/NM` reports
  `"id": null` in the JSON.
- A markup that runs over a page break is two annotations: a colour or author change reaches the
  tail on the next page too (found through its `/NM` link); the tail has no comment of its own.
- **Changing the `author` changes how re-applying finds the annotation.** Dedupe matches on author,
  and a reply note on parent + author + text, so a plan that still names the old author will add a
  second annotation (or reply) rather than match the renamed one. Update the plan's `author` too.
- A failed `update` entry still echoes its `update` id, so results can be matched by id.
- The result has `"action": "updated"` (or `unchanged` if nothing differed) and echoes `update`.

## Quote rules

The `quote` field is the only link between your editorial judgment and the PDF's physical geometry — RailMark resolves it against the page's extracted text to find where to draw the markup. Rules:

- Must be an **exact substring** of the page's text, but matching normalises both sides first, so it is case-insensitive and tolerant of:
  - whitespace differences — extra/collapsed spaces, tabs, line-wraps;
  - **ligatures** — quoting `the first difficulty` matches a PDF containing `the ﬁrst diﬃculty`;
  - **smart punctuation** — straight `"` and `'` match curly `“ ” ‘ ’`, and `...` matches `…`;
  - **wrap hyphens** — quoting `interpretable in practice` matches a PDF that breaks it as `inter-\npretable in practice`. The resulting markup correctly spans both lines.

  It is **not** tolerant of paraphrasing, reordering, or genuinely different punctuation (a comma you added, a dash you changed).
- Must be **contiguous**. It may run over **one page break**: if the quote isn't found whole on `page`, RailMark tries splitting it so the first part ends the last line of body text on `page` and the rest begins a line at the top of the next page — skipping a running footer/header of up to ~300 characters in between, and rejoining a word hyphenated over the break. Each part must be at least 3 characters. The result reports `endPage`. A quote that is simply on a neighbouring page is **not** split: it fails with `Quote not found on page N (it occurs on page M)`, which usually means `page` is off by one. For best results still keep quotes to one sentence or phrase rather than a whole paragraph (long quotes are more likely to hit a formatting difference somewhere in the middle).
- Must come from **prose**, not the `--export` Markdown's own formatting — don't include `**bold**` markers, heading `#` characters, or table pipe characters as part of the quote; those are Markdown, not PDF text.
- Type of markup that spans a single word (e.g. `underline` on a term) resolves most reliably. Long strikeouts across multiple sentences are more failure-prone.

## Repeated quotes

A short quote — a term, a figure, a common phrase — often occurs more than once on a page. Without
more information RailMark marks the **first** match and flags the entry with a `warning`
(`"Ambiguous: the quote occurs 3 times on page 4; marked the first. …"`). Every result reports
`matchCount`, the number of times the quote occurs on the page.

To mark a different occurrence, either:

- set `occurrence` — `2` for the second match in reading order, and so on; or
- set `contextBefore` and/or `contextAfter` — a few words that sit right next to the quote. Only
  matches with that context are candidates. Context is matched with the same normalisation as the
  quote, and the whitespace between quote and context doesn't matter.

Both can be combined: `occurrence` then counts among the matches that have the context. An
`occurrence` larger than the number of candidates, or a context no match has, fails the entry.

A longer quote is often the simplest fix: quote enough words that they occur only once.

## Suggested replacements (`replace`)

`"type": "replace"` with `"replacement": "…"` strikes through the quote and records the text that
should go in its place — a copy-editor's "change X to Y". It is written as a strikeout whose popup
reads `Replace with: <replacement>`, followed by your `comment` if any, so it is legible in any PDF
viewer. `railmark`'s annotation mode reads it back as a single *suggested replacement*
(`~~old~~ → **new**`).

## Replies

An entry with `inReplyTo` answers an existing annotation — typically a human reviewer's comment.
Get the ids from annotation mode's JSON output:

```
railmark <pdf> --format json -o annotations.json
```

Each record there has an `id`, its `page`, `quote` and `comment`. To reply:

```json
{ "page": 2, "type": "note", "inReplyTo": "<id>", "comment": "Agreed — the mean, per section 3." }
```

- A reply `note` needs **no `quote`**: it is pinned directly beneath the note it answers (and
  any earlier replies), or beside the text markup it answers. Other notes in the way are pushed
  down, so a thread stays together and reads top to bottom.
- A reply may instead carry a `quote` (any type), which marks that text as usual and links it to
  the parent.
- `page` must be the page the parent annotation is on.
- Replies are recognised on re-apply by what they answer and what they say, so re-running a plan
  doesn't duplicate them, while two different replies to the same annotation are both kept.

Annotation mode shows replies threaded under their parent, and its JSON reports `inReplyTo`.

## Result report

`railmark --apply-markup` prints a JSON array to **stdout**, one object per plan entry, in plan order:

```json
[
  { "page": 3, "quote": "the null hypothesis is rejected at p < 0.01", "type": "Highlight", "success": true,  "error": null, "spanCount": 1, "matchCount": 1, "action": "added" },
  { "page": 4, "quote": "the model", "type": "Underline", "success": true, "error": null, "spanCount": 1, "matchCount": 3, "action": "added",
    "warning": "Ambiguous: the quote occurs 3 times on page 4; marked the first. Set occurrence, add contextBefore/contextAfter, or lengthen the quote." },
  { "page": 6, "quote": "carried over the page", "type": "Highlight", "success": true, "error": null, "spanCount": 2, "matchCount": 1, "action": "unchanged", "endPage": 7 },
  { "page": 5, "quote": "some paraphrased text", "type": "Note", "success": false, "error": "Quote not found on page 5.", "spanCount": 0, "matchCount": 0 }
]
```

| Field | Meaning |
|-------|---------|
| `success` / `error` | Whether the entry resolved and was applied; the reason if not. |
| `spanCount` | On success, the number of visual-line spans the quote covers (1 for a quote on one line, 2+ when it wraps, across both pages for a quote over a page break). A long quote reporting `spanCount: 1` likely matched less text than you expected. `0` for failed entries. |
| `matchCount` | How many times the quote occurs on the page. Above 1 means the quote is ambiguous — check `warning`. |
| `warning` | Present when the entry was applied but may not have landed where you meant — currently, an ambiguous quote. Treat it like a failure to fix. |
| `action` | On success: `added`, `unchanged` or `updated` (see "Re-applying a plan"). |
| `endPage` | Present when the quote ran over a page break: the page it ends on. |
| `inReplyTo` | Echoed for reply entries. |
| `update` | Echoed for `update` entries (and for a failed one); `quote` and `type` are then absent. |
| `context` | **`--dry-run` only.** The page text around the match, with the match in `[[…]]` — for a split quote, both halves, separated by `‖`. Read it to confirm the quote landed on the passage you meant. |

A human-readable summary, per-failure lines and per-warning lines are printed to **stderr**. Exit code `0` means every entry resolved (and was written, unless `--dry-run` was passed); exit code `2` means one or more entries failed to resolve — well-formed entries are still applied. Exit code `1` is a hard error (bad JSON, missing file, or a write failure) and nothing is written. Warnings do not change the exit code.

Run `railmark --version` to check the installed version when filing a bug report.
