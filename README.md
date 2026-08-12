# RailMark

A command-line tool that converts PDFs to structured Markdown, and writes AI-authored markup back into them. It has three modes:

- **Annotation mode** (default) — extracts annotations from PDFs reviewed in [RailReader2](https://github.com/sjvrensburg/railreader2) (highlights, notes, rectangles, freehand, carets, free-text) and groups them under document headings.
- **Export mode** (`--export`) — produces layout-aware full-document Markdown from *any* PDF, with optional VLM transcription of figures. RailReader2 annotations are folded in when present, but are not required.
- **Apply-markup mode** (`--apply-markup`) — writes real PDF annotations (highlight, underline, strikeout, squiggly, margin notes) from a JSON markup plan. Designed for an AI agent acting as a technical editor: quote a passage, say what kind of markup and why, and RailMark resolves the quote to exact PDF geometry and writes it in. See [Skill for AI agents](#skill-for-ai-agents) below.

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
- Highlights appear **bold** within their surrounding text context (fuzzy whitespace matching)
- Strikeouts and carets read as editorial intent — a strikeout is a *suggested deletion*, a caret a *suggested insertion*, and a caret paired with a strikeout collapses into a single *suggested replacement*
- Text notes and reviewer comments are rendered as blockquotes; a comment that merely repeats the text it covers (as Skim writes it) is dropped rather than printed twice
- Cleans PDF text-extraction artifacts: soft hyphens, control characters, ligatures (`ﬁ` → `fi`), smart quotes, and words split across a line break by a wrap hyphen
- Optional cropped screenshots for rectangle and freehand annotations
- Page-range and colour filtering
- `--export` mode for layout-aware full-document Markdown from any PDF, with optional VLM figure transcription
- `--apply-markup` mode writes highlight/underline/strikeout/squiggly/note annotations into a PDF from a JSON plan, for AI-agent-driven document review

## Usage

```
railmark <pdf> [options]

Options:
  -o <path>            Output file (default: <pdf-stem>-annotations.md). Use - for stdout
  --pages <range>      Only include annotations from these pages (e.g. "1,3,5-10")
  --color <hex>        Filter by annotation colour (e.g. "#FF0000" or "ff0000,ffcc00")
  --images             Include cropped screenshots for rect/freehand annotations
  --export             Export full document to Markdown (layout-aware, includes annotations)
  --no-vlm             Disable VLM transcription (with --export)
  --vlm-endpoint <url> Override VLM endpoint URL (with --export)
  --vlm-model <name>   Override VLM model name (with --export)
  --vlm-api-key <key>  Override VLM API key (with --export)
  --apply-markup <plan.json>
                       Write PDF markup (highlight/underline/strikeout/note) from a
                       JSON markup plan. Reports per-entry results as JSON on stdout.
  --dry-run            With --apply-markup, resolve and report only; do not write
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
tolerates whitespace differences but not paraphrasing). RailMark resolves each quote to its
precise on-page geometry and writes a real PDF annotation. Results are printed as JSON to
stdout — one object per entry, `success`/`error` — so a driving script or agent can detect and
retry any quote that didn't resolve. Use `--dry-run` to check a plan without writing anything.
See `.claude/skills/railmark-markup-plan/references/markup-plan-schema.md` for the full schema.

## Skill for AI agents

For Claude Code / agent-driven document review, install the `railmark-markup-plan` skill from
this repo:

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
dotnet test
```

## License

MIT
