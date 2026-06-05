# Rr2Annotate

A command-line tool that extracts annotations from PDFs reviewed in [RailReader2](https://github.com/sjvrensburg/railreader2) and produces structured Markdown documents. Designed for feeding annotated documents to AI for summarisation or explanation.

## Install

Linux (x86-64, no .NET required):

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/sjvrensburg/rr2parser/main/install.sh)
```

This downloads rr2annotate and the RailReader2 CLI, and writes the config file. Then:

```bash
rr2annotate document.pdf -o notes.md
```

<details>
<summary>Build from source</summary>

Requires [.NET 10+](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet run --project Rr2Annotate/ -- --configure
```

Or install as a .NET tool:

```bash
dotnet pack Rr2Annotate/
dotnet tool install --global --add-source Rr2Annotate/nupkg Rr2Annotate
```

</details>

## Features

- Extracts all annotation types: highlights, underlines, strikeouts, squiggly, text notes, rectangles, freehand drawings, carets, and free-text
- Groups annotations under document headings from the PDF outline
- Arranges content in reading order
- Summary table at the top with annotation counts per section
- Highlights appear **bold** within their surrounding text context (fuzzy whitespace matching)
- Text notes and reviewer comments are rendered as blockquotes
- Deduplicates block text when multiple annotations overlap the same paragraph
- Cleans PDF text extraction artifacts (soft hyphens, control characters)
- Optional cropped screenshots for rectangle and freehand annotations
- Page range and colour filtering
- `--export` mode for full-page Markdown via RailReader2
- `--context` mode to enrich highlights with surrounding page text

## Usage

```
rr2annotate <pdf> [options]

Options:
  -o <path>            Output markdown file (default: <pdf-stem>-annotations.md). Use - for stdout
  --pages <range>      Only include annotations from these pages (e.g. "1,3,5-10")
  --color <hex>        Filter by annotation colour (e.g. "#FF0000" or "ff0000,ffcc00")
  --images             Include cropped screenshots for rect/freehand annotations
  --export             Delegate to RailReader2 export (full-page Markdown)
  --context            Enrich highlights with full page context from RailReader2 export
  --no-vlm             Disable VLM transcription (with --export or --context)
  --vlm-endpoint <url> Override VLM endpoint URL
  --vlm-model <name>   Override VLM model name
  --vlm-api-key <key>  Override VLM API key
  --configure          Set or update the path to the RailReader2 CLI
  -h, --help           Show this help
```

### Text-only export

```bash
rr2annotate document.pdf -o notes.md
```

### With images

```bash
rr2annotate document.pdf -o notes.md --images
```

This creates `notes.md` and a `notes-images/` directory with cropped screenshots of rectangle and freehand annotations.

### Specific pages or colours

```bash
rr2annotate document.pdf --pages "1,3,5-10"
rr2annotate document.pdf --color "#FF0000"
rr2annotate document.pdf --color "ff0000,ffcc00"
```

### Full-page Markdown export

```bash
rr2annotate document.pdf --export
```

## Prerequisites

- [RailReader2 CLI](https://github.com/sjvrensburg/railreader2) **v3.16.0 or later** (the install script handles this)

## Run tests

```bash
dotnet test
```

## License

MIT
