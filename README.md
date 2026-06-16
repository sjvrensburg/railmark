# RailMark

A command-line tool that converts PDFs to structured Markdown. It has two modes:

- **Annotation mode** (default) — extracts annotations from PDFs reviewed in [RailReader2](https://github.com/sjvrensburg/railreader2) (highlights, notes, rectangles, freehand, carets, free-text) and groups them under document headings.
- **Export mode** (`--export`) — produces layout-aware full-document Markdown from *any* PDF, with optional VLM transcription of figures. RailReader2 annotations are folded in when present, but are not required.

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

Or grab the AppImage manually from [Releases](https://github.com/sjvrensburg/railmark/releases) and put it anywhere on your `$PATH`.

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

Build the AppImage:

```bash
./build-appimage.sh [--include-model <onnx-model>]
```

</details>

## Features

- Extracts all annotation types: highlights, underlines, strikeouts, squiggly, text notes, rectangles, freehand drawings, carets, and free-text
- Groups annotations under document headings from the PDF outline, in reading order
- Summary table at the top with annotation counts per section
- Highlights appear **bold** within their surrounding text context (fuzzy whitespace matching)
- Text notes and reviewer comments are rendered as blockquotes
- Cleans PDF text-extraction artifacts (soft hyphens, control characters)
- Optional cropped screenshots for rectangle and freehand annotations
- Page-range and colour filtering
- `--export` mode for layout-aware full-document Markdown from any PDF, with optional VLM figure transcription

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

## Run tests

```bash
dotnet test
```

## License

MIT
