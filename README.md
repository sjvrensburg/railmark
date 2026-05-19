# Rr2Annotate

A .NET command-line tool that extracts annotations from PDFs reviewed in [RailReader2](https://github.com/sjvrensburg/railreader2) and produces structured Markdown documents. Designed for feeding annotated documents to AI for summarisation or explanation.

## Features

- Extracts all annotation types: highlights, text notes, rectangles, and freehand drawings
- Groups annotations under document headings from the PDF outline
- Arranges content in reading order
- Summary table at the top with annotation counts per section
- Highlights appear **bold** within their surrounding text context (fuzzy whitespace matching)
- Text notes are rendered as blockquotes with the nearby document text
- Deduplicates block text when multiple annotations overlap the same paragraph
- Cleans PDF text extraction artifacts (soft hyphens, control characters)
- Optional cropped screenshots for rectangle and freehand annotations
- Page range filtering to export only specific pages
- Colour filtering to export only annotations of specific colours

## Prerequisites

- [.NET 10+](https://dotnet.microsoft.com/download)
- [RailReader2 CLI](https://github.com/sjvrensburg/railreader2) **v3.12.0.0 or later** installed and accessible (earlier versions lack the `export` and `vlm` subcommands used by `--export` and `--context`)

## Setup

Build the project:

```bash
dotnet build
```

Configure the path to your RailReader2 CLI on first run (or any time with `--configure`):

```bash
dotnet run --project Rr2Annotate/ -- --configure
```

This stores your CLI command in `~/.config/rr2annotate/settings.json`. You can point it to a wrapper script, a direct binary path, or any command that invokes the RailReader2 CLI.

## Usage

```
rr2annotate <pdf> [options]

Options:
  -o <path>       Output markdown file (default: <pdf-stem>-annotations.md)
  --pages <range> Only include annotations from these pages (e.g. "1,3,5-10")
  --color <hex>   Filter by annotation colour (e.g. "#FF0000" or "ff0000,ffcc00")
  --images        Include cropped screenshots for rect/freehand annotations
  --configure     Set or update the path to the RailReader2 CLI
  -h, --help      Show this help
```

### Text-only export

```bash
dotnet run --project Rr2Annotate/ -- document.pdf -o notes.md
```

### With images

```bash
dotnet run --project Rr2Annotate/ -- document.pdf -o notes.md --images
```

This creates `notes.md` and a `notes-images/` directory with cropped screenshots of rectangle and freehand annotations.

### Specific pages only

```bash
dotnet run --project Rr2Annotate/ -- document.pdf -o notes.md --pages "1,3,5-10"
```

### Filter by colour

```bash
# Only red annotations
dotnet run --project Rr2Annotate/ -- document.pdf --color "#FF0000"

# Multiple colours
dotnet run --project Rr2Annotate/ -- document.pdf --color "ff0000,ffcc00"
```

### Run tests

```bash
dotnet test
```

### Install as a global tool

```bash
dotnet pack Rr2Annotate/
dotnet tool install --global --add-source Rr2Annotate/nupkg Rr2Annotate
```

Then use directly:

```bash
rr2annotate document.pdf -o notes.md --images
```

## License

MIT
