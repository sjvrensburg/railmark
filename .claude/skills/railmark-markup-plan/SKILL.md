---
name: railmark-markup-plan
description: >-
  Produces and applies a structured PDF review-markup plan (highlight/underline/strikeout/note)
  against a PDF using the railmark CLI, resolving AI-agent-quoted text to exact PDF annotation
  geometry and writing native PDF annotations. Use when asked to review, mark up, annotate, or
  leave editorial comments on a PDF as a technical editor or proofreader — e.g. "mark up this
  PDF", "add review comments to this document", "highlight the key claims and flag errors in
  this paper", "give feedback on this student's submission as tracked PDF markup". Not for
  extracting existing annotations from a PDF (use railmark's default mode) or for producing a
  plain Markdown summary of a PDF (use railmark --export without --apply-markup).
---

# RailMark markup plan

RailMark can write real, native PDF annotations (highlights, underlines, strikeouts, margin
notes) from a JSON "markup plan" you author. This skill is the workflow for acting as a
technical editor on a PDF: read it, decide what to mark up, express that as a markup plan, and
apply it.

## Workflow

1. **Export the PDF to Markdown** so you have page-anchored text to read and quote from:
   ```
   railmark <pdf> --export -o notes.md
   ```
   The Markdown preserves page breaks — note which page each passage you plan to mark up comes
   from.

2. **Read `notes.md` and act as a technical editor.** Decide what's worth marking up. See
   "Editorial conventions" below for which annotation type fits which kind of feedback.

3. **Draft a markup plan JSON file** following `references/markup-plan-schema.md`. A worked
   example is at `templates/example-markup-plan.json`.

4. **Always dry-run the full plan first:**
   ```
   railmark <pdf> --apply-markup plan.json --dry-run
   ```
   This is not optional — it's the cheap way to catch a plan where most quotes fail to resolve
   before writing anything. Read the JSON result (see step 5) and fix any failures before
   proceeding.

5. **Apply for real, then read the JSON result printed to stdout again.** Each entry reports
   `success` and, on failure, an `error`. If any entry failed, don't retry the identical quote —
   re-read that page's exact text (re-run `--export`, or re-open `notes.md` at that page) and
   correct the quote before retrying just the failed entries. To fix a handful of failures
   without duplicating the entries that already succeeded, run a second `--apply-markup` with a
   plan containing **only** the corrected entries — applying is additive, so re-running the whole
   plan would duplicate every already-successful annotation.

## The most important rule: quotes must be verbatim

`quote` is resolved against the PDF's actual extracted text, not against your memory or
paraphrase of it. The single biggest cause of failed entries is a quote that doesn't exactly
match the source:

- Copy the phrase as it literally appears in the prose — don't paraphrase, reorder, or "clean
  up" punctuation.
- Don't include Markdown formatting characters (`**`, `#`, `|`) from the `--export` output —
  those are Markdown artifacts, not PDF text.
- Prefer quoting from prose paragraphs over headings or tables, where layout reflow is more
  likely to introduce a mismatch.
- Keep quotes reasonably short (a phrase or sentence, not a full paragraph) — longer quotes are
  more likely to hit a whitespace or hyphenation difference somewhere in the middle.

Full schema details, matching rules, and the result-report format are in
`references/markup-plan-schema.md` — read it before drafting your first plan.

## Editorial conventions

Use each annotation type the way a real technical editor would, so the markup reads as
intentional rather than exhaustive:

- **`highlight`** — a claim, number, or statement worth the reader double-checking (a cited
  statistic, a causal claim, a key result).
- **`underline`** — a term or definition worth flagging (jargon, an ambiguous or undefined term,
  something the reader should look up or the author should define).
- **`strikeout`** — something you're confident is a factual or logical error.
- **`note`** — substantive commentary that doesn't fit as a short comment on a highlight/
  underline/strikeout — a structural suggestion, a request to reconsider an argument, a
  cross-reference to another part of the document.

Aim for the density of a real edit: roughly one entry per notable issue or claim per page, not
one per sentence. Marking everything is as unhelpful to the reader as marking nothing.

## If entries fail

`--apply-markup` reports failures without aborting the rest of the plan (exit code `2` means
"some entries failed, the rest were still applied"). For each failure:

1. Re-read the exact text of that page (don't trust your first reading of it).
2. Correct the quote to match verbatim.
3. Re-run `--apply-markup` with just the corrected entries (or the whole plan again — applying
   is additive, so re-running previously-successful entries will duplicate them; prefer a
   smaller follow-up plan containing only the fixed entries).
