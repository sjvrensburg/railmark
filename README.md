# RailMark

**Website:** https://sjvrensburg.github.io/railmark/

A command-line tool that converts PDFs to structured Markdown, and writes AI-authored markup back into them. It has three modes:

- **Annotation mode** (default) — extracts annotations from *any* annotated PDF (highlights, underlines, strikeouts, squigglies, notes, rectangles, freehand, carets, free-text) and groups them under document headings, as Markdown or, with `--format json`, as structured records an agent can act on. It reads the annotations the PDF itself carries, so it does not matter which tool wrote them — Acrobat, Preview, Okular, Zotero, [RailReader2](https://github.com/sjvrensburg/railreader2), or RailMark's own `--apply-markup`. RailReader2 sidecar files are picked up too, for PDFs that cannot be written to.
- **Export mode** (`--export`) — produces layout-aware full-document Markdown from *any* PDF, with optional VLM transcription of figures. Existing annotations are folded in when present, but are not required.
- **Apply-markup mode** (`--apply-markup`) — writes real PDF annotations (highlight, underline, strikeout, squiggly, suggested replacements, margin notes, and replies to existing comments) from a JSON markup plan. Designed for an AI agent acting as a technical editor: quote a passage, say what kind of markup and why, and RailMark resolves the quote to exact PDF geometry and writes it in. See [Skill for AI agents](#skill-for-ai-agents) below.

Built directly on the [RailReader.Core](https://github.com/sjvrensburg/RailReaderCore) NuGet packages — no external CLI required. Distributed as a self-contained AppImage for Linux x86-64.

## Install

Linux (x86-64, no .NET required):

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/sjvrensburg/railmark/main/install.sh)
```

This downloads the self-contained AppImage to `~/.local/bin/railmark`. Then:

```bash
railmark document.pdf -o notes.md
```

Or grab a build manually from [Releases](https://github.com/sjvrensburg/railmark/releases). Every release ships three artifacts, each self-contained (bundling the .NET runtime, the native libraries, and the ONNX layout model — nothing else to install) and each verifiable against the release's `SHA256SUMS`:

| Artifact | Platform | Notes |
|----------|----------|-------|
| `railmark-<version>-linux-x86_64.AppImage` | Linux x86-64 | Recommended. Single executable; needs FUSE, or run with `--appimage-extract-and-run`. |
| `railmark-<version>-linux-x86_64.tar.gz` | Linux x86-64 | Fallback for containers and images without FUSE. Extract, then run the `railmark` launcher at the package root. |
| `railmark-<version>-win-x64.zip` | Windows x64 | Extract and run `railmark.exe`. |

<details>
<summary>Linux tarball install</summary>

```bash
mkdir -p ~/.local/opt ~/.local/bin
tar -xzf railmark-<version>-linux-x86_64.tar.gz -C ~/.local/opt/
ln -sf ~/.local/opt/railmark-<version>-linux-x86_64/railmark ~/.local/bin/railmark
```

</details>

<details>
<summary>Build from source</summary>

Requires [.NET 10+](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet run --project RailMark/ -- document.pdf -o notes.md
```

Or install as a .NET tool:

```bash
dotnet pack RailMark/
dotnet tool install --global --add-source RailMark/nupkg RailMark
```

Build the release artifacts. Both scripts take the same optional `--include-model`; without it, `--export` falls back to plain text instead of layout analysis:

```bash
./scripts/download-model.sh                                   # fetch + verify PP-DocLayoutV3.onnx
./build-appimage.sh  --include-model models/PP-DocLayoutV3.onnx
./build-tarball.sh   --include-model models/PP-DocLayoutV3.onnx
```

Windows packages are produced by CI; a local `dotnet publish -r win-x64 --self-contained` gives the same binary, with the model placed in a `models/` directory beside `railmark.exe`.

</details>

<details>
<summary>Releases</summary>

Pushing a `v*` tag runs [`.github/workflows/release.yml`](.github/workflows/release.yml), which tests, builds all three artifacts, smoke-tests each one, and opens a **draft** release with them and a `SHA256SUMS` file attached — so release notes can be written by hand before publishing.

The tag must match `<Version>` in `RailMark.csproj` and `VERSION` in `install.sh`; the workflow fails early if they disagree. `workflow_dispatch` runs the same build without creating a release.

</details>

## Features

- Extracts all annotation types: highlights, underlines, strikeouts, squiggly, text notes, rectangles, freehand drawings, carets, and free-text
- Groups annotations under document headings from the PDF outline, in reading order — including two headings on the same page, which are split by their position down the page
- Summary table at the top with annotation counts per section
- Highlights appear **bold** within their surrounding text context (fuzzy whitespace matching) — on the occurrence actually marked, when the same words appear more than once on the page
- Strikeouts and carets read as editorial intent — a strikeout is a *suggested deletion*, a caret a *suggested insertion*, and a caret paired with a strikeout collapses into a single *suggested replacement*
- Replies are threaded under the comment they answer, and a markup that runs over a page break reads as one entry
- Text notes and reviewer comments are rendered as blockquotes; a comment that merely repeats the text it covers (as Skim writes it) is dropped rather than printed twice
- Cleans PDF text-extraction artifacts: soft hyphens, control characters, ligatures (`ﬁ` → `fi`), smart quotes, and words split across a line break by a wrap hyphen
- Optional cropped screenshots for rectangle and freehand annotations
- Page-range, colour, author and date filtering (`--author`, `--exclude-author`, `--since`)
- `--format json` for machine-readable output: one record per annotation, shaped like a markup-plan entry, with its id for replying
- `--export` mode for layout-aware full-document Markdown from any PDF, with optional VLM figure transcription
- `--apply-markup` mode writes highlight/underline/strikeout/squiggly/replace/note annotations and replies into a PDF from a JSON plan, for AI-agent-driven document review. Quotes can say which occurrence they mean, may run over a page break, and re-applying a plan never duplicates markup

## Usage

```
railmark <pdf> [options]

Options:
  -o <path>            Output file (default: <pdf-stem>-annotations.md). Use - for stdout
  --pages <range>      Only include annotations from these pages (e.g. "1,3,5-10")
  --color <hex>        Filter by annotation colour (e.g. "#FF0000" or "ff0000,ffcc00")
  --author <names>     Only include annotations by these authors (comma-separated,
                       case-insensitive)
  --exclude-author <names>
                       Leave out annotations by these authors (e.g. "AI Reviewer" to
                       see only the human markup)
  --since <date>       Only include annotations created or last modified on or after
                       <date> (e.g. 2026-09-01 or 2026-09-01T14:30; local time unless
                       an offset is given). Annotations without a date are left out
  --images             Include cropped screenshots for rect/freehand annotations
  --format <markdown|json>
                       Annotation-mode output format (default: markdown). json emits
                       one record per annotation, shaped like a markup-plan entry
                       (default output: <pdf-stem>-annotations.json)
  --export             Export full document to Markdown (layout-aware, includes annotations)
  --no-vlm             Disable VLM transcription (with --export)
  --vlm-endpoint <url> Override VLM endpoint URL (with --export)
  --vlm-model <name>   Override VLM model name (with --export)
  --vlm-api-key <key>  Override VLM API key (with --export)
  --accelerator <cpu|gpu>
                       Layout-model inference backend (with --export, default: cpu).
                       gpu opportunistically uses a native WebGPU execution provider
                       and falls back to cpu if no device is available.
  --apply-markup <plan.json>
                       Write PDF markup (highlight/underline/strikeout/squiggly/
                       replace/note) from a JSON markup plan (- reads it from stdin)
                       into a new PDF (default: <pdf-stem>-marked.pdf,
                       or -o <path>). The input PDF is never modified unless --in-place
                       is given. Reports per-entry results as JSON on stdout.
  --in-place           With --apply-markup, write annotations directly into the input
                       PDF instead of a copy. Cannot be combined with -o.
  --replace-author <name>
                       With --apply-markup, first remove every existing annotation
                       by <name> — to revise an earlier round of markup rather than
                       add to it. (Re-applying a plan never duplicates identical
                       markup either way.)
  --dry-run            With --apply-markup, resolve and report only; do not write.
                       The report then also shows the text around each match
  --version            Show the railmark version
  -h, --help           Show this help
```

### Annotations to Markdown

```bash
railmark document.pdf -o notes.md
```

### With images

```bash
railmark document.pdf -o notes.md --images
```

This creates `notes.md` and a `notes-images/` directory with cropped screenshots of rectangle and freehand annotations.

### Specific pages or colours

```bash
railmark document.pdf --pages "1,3,5-10"
railmark document.pdf --color "#FF0000"
railmark document.pdf --color "ff0000,ffcc00"
```

### Structured output for agents

```bash
railmark document.pdf --format json -o annotations.json
railmark document.pdf --format json --exclude-author "AI Reviewer" -o -   # only the human markup
railmark document.pdf --since 2026-09-01                                  # only what changed since
```

Each JSON record carries the annotation's `page`, `type`, `quote`, `comment`, `author`,
`heading` and `id`. The fields match a markup-plan entry, so records can be fed straight back
into `--apply-markup`, or answered with a reply that names the record's `id`.

### Full-document export (any PDF)

```bash
railmark document.pdf --export
```

### Apply AI-authored markup

```bash
railmark document.pdf --apply-markup plan.json
```

`plan.json` is a JSON object listing markup entries — page, an exact quote to locate, the
annotation type, and an optional comment:

```json
{
  "entries": [
    { "page": 3, "quote": "the null hypothesis is rejected at p < 0.01", "type": "highlight",
      "comment": "Key statistical claim — verify against Table 2." },
    { "page": 5, "quote": "heteroscedasticity", "type": "underline",
      "comment": "Domain term — confirm reader familiarity." }
  ]
}
```

Each entry's `quote` must be an exact, verbatim substring of the PDF's extracted text (matching
tolerates whitespace, ligature, smart-quote and wrap-hyphen differences but not paraphrasing).
RailMark resolves each quote to its precise on-page geometry and writes a real PDF annotation.
Results are printed as JSON to stdout — one object per entry, `success`/`error` — so a driving
script or agent can detect and retry any quote that didn't resolve. Use `--dry-run` to check a
plan without writing anything; it also shows the text around each match.

Beyond the basics:

- **Repeated quotes.** A quote that occurs more than once on its page is flagged with a
  `warning`; say which match you mean with `occurrence` or `contextBefore`/`contextAfter`.
- **Page breaks.** A quote may run from the bottom of one page onto the next, past a running
  footer or header.
- **Suggested replacements.** `"type": "replace"` with a `"replacement"` strikes the quote
  through and records the text that should replace it.
- **Replies.** `"inReplyTo": "<id>"` (ids come from `--format json`) answers an existing comment;
  a reply note needs no quote.
- **Re-applying is safe.** Markup already present is reported `unchanged` rather than duplicated,
  and a changed comment is `updated` in place. `--replace-author <name>` clears an earlier round
  first.

See `.claude/skills/railmark-markup-plan/references/markup-plan-schema.md` for the full schema.

## Skill for AI agents

For Claude Code / agent-driven document review, use the `railmark-markup-plan` skill.

**Easiest way:** point Claude Code at the skill file and ask it to install it — the file carries
its own setup instructions, covering both the CLI and the skill itself, for Linux and Windows:

```
Install this skill: https://github.com/sjvrensburg/railmark/blob/main/.claude/skills/railmark-markup-plan/SKILL.md
```

Manually, from a clone of this repo:

```bash
mkdir -p ~/.claude/skills
cp -r .claude/skills/railmark-markup-plan ~/.claude/skills/
```

(Or, working inside a clone of this repo, the skill is already available project-scoped at
`.claude/skills/railmark-markup-plan/` — no copy needed.) It teaches an agent the full
review-and-apply workflow — exporting the document, drafting a markup plan, the verbatim-quote
requirement, editorial conventions for choosing highlight/underline/strikeout/note, and how to
recover from failed quotes — and is the recommended way to drive `--apply-markup` from an agent
rather than re-deriving the schema from scratch each time.

## Run tests

```bash
dotnet test                                            # unit tests
./tests/integration-smoke.sh dotnet run --project RailMark/ --
```

The integration script drives a real build against `tests/fixtures/sample.pdf` — a small,
synthetic, byte-deterministic PDF generated by `tests/fixtures/make-sample-pdf.py`. It takes any
railmark invocation, so it can check a built artifact just as easily:

```bash
./tests/integration-smoke.sh ./dist/railmark-<version>-linux-x86_64.AppImage
```

CI runs both, and runs the integration checks against each of the three release artifacts.

## Acknowledgements

Several of the text-handling behaviours in annotation mode were adapted from
[**pdfannots**](https://github.com/0xabu/pdfannots) by Andrew Baumann — a Python tool that
extracts PDF annotations to Markdown, and the closest prior art to what RailMark does in its
default mode. Specifically:

- **De-hyphenation across line breaks**, including the guard that keeps a real hyphen when the
  preceding character is not lowercase, so `COVID-19` survives but `inter-`/`pretable` is rejoined.
- **Ligature and smart-quote normalisation** applied to both sides of a text match, which is what
  lets an agent quote `"the first difficulty"` and match a PDF containing the `ﬁ` glyph.
- **Suppressing an annotation comment that merely repeats the text it covers** — Skim writes the
  selected text into `/Contents`, and printing it back is pure duplication.
- **Reading carets and strikeouts as editorial intent** — a lone caret is a suggested insertion, a
  strikeout a suggested deletion, and a caret paired with a strikeout is a suggested replacement.
- **Ordering annotations by position within a page** rather than by page number alone, so two
  headings on one page divide their annotations correctly.

The implementations here are independent — different language, and different underlying PDF
library — but the heuristics and the vocabulary are pdfannots'. It is MIT-licensed, as is
RailMark. If you want annotation extraction without the .NET dependency, or for a workflow
RailMark does not cover, use it directly.

## License

MIT
