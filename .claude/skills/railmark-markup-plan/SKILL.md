---
name: railmark-markup-plan
description: >-
  Produces and applies a structured PDF review-markup plan (highlight/underline/strikeout/
  replace/note, and replies to existing comments) against a PDF using the railmark CLI,
  resolving AI-agent-quoted text to exact PDF annotation geometry and writing native PDF
  annotations. Use when asked to review, mark up, annotate, or
  leave editorial comments on a PDF as a technical editor or proofreader — e.g. "mark up this
  PDF", "add review comments to this document", "highlight the key claims and flag errors in
  this paper", "give feedback on this student's submission as tracked PDF markup", "respond to
  the reviewer's comments on this PDF". Also covers
  installing the railmark CLI and this skill itself — use it for "install railmark", "set up the
  railmark skill", or when railmark is missing from PATH. Not for extracting existing annotations
  from a PDF (use railmark's default mode) or for producing a plain Markdown summary of a PDF
  (use railmark --export without --apply-markup).
---

# RailMark markup plan

RailMark can write real, native PDF annotations (highlights, underlines, strikeouts, suggested
replacements, margin notes, and replies to existing comments) from a JSON "markup plan" you
author. This skill is the workflow for acting as a
technical editor on a PDF: read it, decide what to mark up, express that as a markup plan, and
apply it.

## Setup — check this first

This skill drives the `railmark` CLI, so it cannot work unless that binary is installed. Before
step 1 of the workflow, check:

```bash
railmark --version
```

This copy of the skill documents **railmark 0.17.0 or newer**; older builds lack features it
relies on (`update` by id, the near-duplicate warning, `--opacity`, `--remove-ids`, replies that
carry a real `/IRT`, removal that takes a comment's replies with it, the no-text-layer error and
OCR warning, sharing RailReader2's annotation sidecar). Check the version is recent enough:

```bash
need=0.17.3; have=$(railmark --version | awk '{print $2}')
[ "$(printf '%s\n%s\n' "$need" "$have" | sort -V | head -n1)" = "$need" ] && echo "ok: $have" || echo "too old: $have"
```

If it prints `ok`, skip the rest of this section. If `railmark` is missing, or too old, install
the CLI as below (the Linux installer replaces an existing copy, which is also the upgrade path),
then re-check. Never fall back to a different PDF tool, and never hand-write PDF annotation
geometry — the whole point of this skill is that `railmark` resolves quotes to exact geometry.

### Installing the `railmark` CLI

Pick the line matching the platform. Each is self-contained: no .NET runtime, no external PDF
tool, nothing else to install.

**Linux x86-64 (recommended)** — installs the AppImage to `~/.local/bin/railmark`:

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/sjvrensburg/railmark/main/install.sh)
```

**Linux without FUSE** (containers, minimal images, some CI runners) — use the tarball. AppImages
need FUSE to self-mount; if the AppImage fails with a FUSE or `squashfs` error, this is the fix:

```bash
tag=$(curl -fsSL https://api.github.com/repos/sjvrensburg/railmark/releases/latest \
      | grep -o '"tag_name": *"[^"]*"' | cut -d'"' -f4)
ver=${tag#v}
mkdir -p ~/.local/opt ~/.local/bin
curl -fL "https://github.com/sjvrensburg/railmark/releases/download/${tag}/railmark-${ver}-linux-x86_64.tar.gz" \
  | tar -xz -C ~/.local/opt/
ln -sf ~/.local/opt/railmark-${ver}-linux-x86_64/railmark ~/.local/bin/railmark
```

(An already-installed AppImage can also be run without FUSE via
`railmark --appimage-extract-and-run <args>`.)

**Windows x64** — download `railmark-<version>-win-x64.zip` from
<https://github.com/sjvrensburg/railmark/releases/latest>, extract it, and use `railmark.exe`
from the extracted folder (or add that folder to `PATH`). To upgrade, extract the newer zip over
the old folder.

Every release publishes a `SHA256SUMS` file next to its artifacts; to verify a manual download,
run `sha256sum -c --ignore-missing SHA256SUMS` (Linux) in the folder holding both.

If `~/.local/bin` is not on `PATH`, either add it or invoke the binary by full path. Confirm with
`railmark --version` before continuing.

### Installing this skill

If you are reading this file from a clone or a URL rather than from an installed skill, copy it
into place so it loads automatically in future sessions. Install for the **user** (available in
every project):

```bash
mkdir -p ~/.claude/skills
git clone --depth 1 https://github.com/sjvrensburg/railmark /tmp/railmark-skill
cp -r /tmp/railmark-skill/.claude/skills/railmark-markup-plan ~/.claude/skills/
rm -rf /tmp/railmark-skill
```

Or for a **single project**, copy the same directory into `<project>/.claude/skills/` instead.

Without git, fetch the three files directly — the directory layout matters, since `SKILL.md`
refers to the other two by relative path:

```bash
base=https://raw.githubusercontent.com/sjvrensburg/railmark/main/.claude/skills/railmark-markup-plan
dest=~/.claude/skills/railmark-markup-plan
mkdir -p "$dest/references" "$dest/templates"
curl -fsSL "$base/SKILL.md"                            -o "$dest/SKILL.md"
curl -fsSL "$base/references/markup-plan-schema.md"    -o "$dest/references/markup-plan-schema.md"
curl -fsSL "$base/templates/example-markup-plan.json"  -o "$dest/templates/example-markup-plan.json"
```

To update the skill, repeat either method — it overwrites the three files. Update the CLI and the
skill together, since the schema reference describes the CLI of the same release.

Verify the install: `ls ~/.claude/skills/railmark-markup-plan` should list `SKILL.md`,
`references/` and `templates/`. The skill is picked up in the next Claude Code session; in the
current one, keep following this file directly.

## Workflow

0. **Confirm `railmark --version` works** (see Setup above). Everything below assumes it does.

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
   before writing anything. In the JSON result, fix every entry with `"success": false` **and
   every entry carrying a `warning`** — a warning usually means the quote occurs more than once on the
   page and RailMark had to guess which one you meant (see "Repeated quotes" below). The exception
   is `Matched OCR text on page N`, which no plan change can clear: check that entry's `context`
   instead. A dry run
   also reports each match's surrounding text as `context`, with the match in `[[…]]`: skim it to
   confirm each quote landed on the passage you intended.

5. **Apply for real, then read the JSON result printed to stdout again.** By default this writes
   a new file — `<pdf-stem>-marked.pdf`, or `-o <path>` to name it yourself — and never modifies
   the input PDF. Each entry reports `success` and, on failure, an `error`. If any entry failed,
   don't retry the identical quote — re-read that page's exact text (re-run `--export`, or
   re-open `notes.md` at that page) and correct the quote. Then re-run the **whole** corrected
   plan against the same input: applying is idempotent, so entries that already succeeded report
   `"action": "unchanged"` and are not duplicated.

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

A quote may run over a page break — give the page it starts on, and RailMark splits it (the result
reports `endPage`).

Full schema details, matching rules, and the result-report format are in
`references/markup-plan-schema.md` — read it before drafting your first plan.

## Repeated quotes

A short quote often occurs more than once on its page — a term, a number, "the model". RailMark
then marks the first match and flags the entry with a `warning` and a `matchCount` above 1. Don't
leave that to chance. Either lengthen the quote until it is unique, or say which match you mean:

- `"occurrence": 2` — the second match on the page, in reading order; or
- `"contextBefore": "…"` / `"contextAfter": "…"` — a few words sitting right beside the quote.

Check the dry run's `context` for the entry to confirm the choice.

## Editorial conventions

Use each annotation type the way a real technical editor would, so the markup reads as
intentional rather than exhaustive.

First decide the **scope** of the feedback, because that picks the kind of annotation:

- Feedback about **specific words or a sentence** — a claim, a figure, a term, a wording — goes
  on the text itself: a `highlight`, `underline`, `squiggly`, `strikeout` or `replace` with the
  remark in its `comment`. The reader sees exactly which words you mean.
- Feedback about a **paragraph, section or argument as a whole** — its structure, what it
  leaves out, how it relates to another part of the document — is a `note`. A note is a sticky
  icon placed in the margin beside the quoted line, not on the text, so it reads as "about this
  passage", not "about these words". Quote the paragraph's opening words so the icon sits beside
  its first line.

Don't use a `note` for a remark about one sentence (quote that sentence and highlight it with the
remark as its `comment`), and don't highlight a whole paragraph to carry a remark about the
paragraph (that is a `note`).

Then, within that scope:

- **`highlight`** — a claim, number, or statement worth the reader double-checking (a cited
  statistic, a causal claim, a key result). Put the remark in `comment`.
- **`underline`** — a term or definition worth flagging (jargon, an ambiguous or undefined term,
  something the reader should look up or the author should define).
- **`strikeout`** — something you're confident is a factual or logical error, or text that should
  simply be deleted.
- **`replace`** — a concrete wording fix: the quote is struck through and `replacement` holds the
  text that should replace it (typos, word choice, a corrected figure). Prefer this over a
  strikeout plus a note whenever you know exactly what the text should say.
- **`note`** — commentary on a paragraph or section rather than on particular words: a
  structural suggestion, a request to reconsider an argument, a missing consideration, a
  cross-reference to another part of the document. Several notes on one passage stack beneath
  each other in the margin.

## Responding to existing comments

When the PDF already carries a reviewer's comments and you're asked to answer them, read them as
structured data rather than scraping the export:

```
railmark <pdf> --format json -o annotations.json
```

Each record has an `id`, `page`, `type`, `quote` (the text it marks), `comment` and `author`.
`--exclude-author "AI Reviewer"` shows only the human markup; `--since 2026-09-01` only what changed
since a date. Reply with a `note` entry naming the record's `id` in `inReplyTo` (and its `page`);
no `quote` is needed — the reply is pinned beside the comment it answers:

```json
{ "page": 2, "type": "note", "inReplyTo": "<id>", "comment": "Done — reworded in section 3." }
```

Records are shaped like plan entries, so a record can also be copied into a plan as-is.

## Density

Aim for the density of a real edit: roughly one entry per notable issue or claim per page, not
one per sentence. Marking everything is as unhelpful to the reader as marking nothing.

## If entries fail

`--apply-markup` reports failures without aborting the rest of the plan (exit code `2` means
"some entries failed, the rest were still applied"). For each failure:

1. Re-read the exact text of that page (don't trust your first reading of it).
2. Correct the quote to match verbatim — or, for an `occurrence`/context error, fix the choice of
   match. An error ending `(it occurs on page M)` means the `page` number is wrong. A
   `Conflicts with entry N` error means two entries mark the same text differently: merge them.
   An error ending `whose text comes from OCR …` means the page is a scan with a recognised text
   layer, which may misread a word: quote a shorter stretch that avoids the misread.
3. Re-run the whole corrected plan. Applying is idempotent: already-applied entries come back
   `"action": "unchanged"`, and an entry whose comment you changed comes back `"updated"`.

If entries fail with `Page N has no text layer (is it a scan?)`, don't retry: no quote can match
a page that has no text. Tell the user the PDF needs a text layer first. `railreader2 ocr <pdf>`
writes a copy with one (`<name>_ocr.pdf`), and that copy is the file to export and mark up.

## Revising an earlier round

To replace a previous round of your markup rather than add to it — say, after the author has
revised the document, or when you'd drop some earlier entries — apply the new plan with
`--replace-author "AI Reviewer"` (or the `author` your plan uses). That first removes every
existing annotation by that author, leaving everyone else's untouched.

To change a human reviewer's annotation instead — reword it, recolour it, give it an author —
use an `update` entry with its `id` from `--format json`; it keeps the annotation's position and
works for any type, notes and ink included. `--replace-unauthored` and `--remove-ids` clear
annotations that have no author, or particular ones. See the schema reference.
