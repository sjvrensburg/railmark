---
name: railmark-markup-plan
description: >-
  Produces and applies a structured PDF review-markup plan (highlight/underline/strikeout/note)
  against a PDF using the railmark CLI, resolving AI-agent-quoted text to exact PDF annotation
  geometry and writing native PDF annotations. Use when asked to review, mark up, annotate, or
  leave editorial comments on a PDF as a technical editor or proofreader — e.g. "mark up this
  PDF", "add review comments to this document", "highlight the key claims and flag errors in
  this paper", "give feedback on this student's submission as tracked PDF markup". Also covers
  installing the railmark CLI and this skill itself — use it for "install railmark", "set up the
  railmark skill", or when railmark is missing from PATH. Not for extracting existing annotations
  from a PDF (use railmark's default mode) or for producing a plain Markdown summary of a PDF
  (use railmark --export without --apply-markup).
---

# RailMark markup plan

RailMark can write real, native PDF annotations (highlights, underlines, strikeouts, margin
notes) from a JSON "markup plan" you author. This skill is the workflow for acting as a
technical editor on a PDF: read it, decide what to mark up, express that as a markup plan, and
apply it.

## Setup — check this first

This skill drives the `railmark` CLI, so it cannot work unless that binary is installed. Before
step 1 of the workflow, check:

```bash
railmark --version
```

If that prints a version, skip the rest of this section. If it errors, install the CLI as below,
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
from the extracted folder (or add that folder to `PATH`).

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
   before writing anything. Read the JSON result (see step 5) and fix any failures before
   proceeding.

5. **Apply for real, then read the JSON result printed to stdout again.** By default this writes
   a new file — `<pdf-stem>-marked.pdf`, or `-o <path>` to name it yourself — and never modifies
   the input PDF. Each entry reports `success` and, on failure, an `error`. If any entry failed,
   don't retry the identical quote — re-read that page's exact text (re-run `--export`, or
   re-open `notes.md` at that page) and correct the quote before retrying just the failed
   entries. To fix a handful of failures without duplicating the entries that already succeeded,
   run a second `--apply-markup` with a plan containing **only** the corrected entries, applying
   on top of the previously-produced `-marked.pdf` (pass it as the `<pdf>` argument) — applying is
   additive, so re-running the whole plan against the same output would duplicate every
   already-successful annotation.

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
3. Re-run `--apply-markup` against the `-marked.pdf` produced by the previous run, with just the
   corrected entries (or the whole plan again — applying is additive, so re-running
   previously-successful entries will duplicate them; prefer a smaller follow-up plan containing
   only the fixed entries).
