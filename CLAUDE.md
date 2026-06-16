# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A .NET tool (**RailMark**) that converts PDFs to Markdown suitable for AI summarisation. Two modes: annotation mode extracts annotations from PDFs reviewed in RailReader2; export mode (`--export`) produces layout-aware full-document Markdown from any PDF. Uses the **RailReader.Core NuGet packages** directly — no external CLI required. Distributed as a self-contained AppImage for Linux x86-64.

## Build & Test

- **Build:** `dotnet build`
- **Test:** `dotnet test`
- **Single test:** `dotnet test --filter "FullyQualifiedName~TestName"`
- **Run:** `dotnet run --project RailMark/ -- <pdf> [-o output.md] [--pages] [--color] [--images] [--export]`
- **Pack (AppImage):** `./build-appimage.sh [--include-model <path>]`

Solution file is `RailMark.slnx` (new XML `.slnx` format, not traditional `.sln`).

## Architecture

`RailMark/` is a .NET console app (top-level statements in `Program.cs`), `RailMark.Tests/` is the xUnit test project. All services are static classes — no dependency injection.

### Pipeline (Program.cs)

**Annotation mode (default):**
1. `PdfiumResolver.Initialize()` + `SkiaPdfServiceFactory` init
2. Manual arg parsing → `CompositeAnnotationStore.Default.Load(pdf)` → page/color filter
3. `PdfTextService.ExtractPageText()` per page → `ExtractTextInRect()` per highlight rect
4. Optional `ScreenshotService.CropAnnotationsAsync()` for `--images`
5. `MarkdownBuilder.Build()` → write file

**Export mode (`--export`):**
1. `PdfiumResolver.Initialize()` + `SkiaPdfServiceFactory` init
2. `MarkdownExportService.ExportAsync()` — full layout-aware pipeline with optional VLM

### Models

Annotation types come from `RailReader.Core.Models` via `[JsonPolymorphic]` — no hand-written converter. Key types: `AnnotationFile` (container; `Dictionary<int, List<Annotation>> Pages`, 0-based keys), `HighlightAnnotation`, `TextNoteAnnotation`, `RectAnnotation`, `FreehandAnnotation`, `CaretAnnotation`, `FreeTextAnnotation`.

Outline from `IPdfService.Outline` → `List<OutlineEntry>` (Title, Page?, Children).

### Services

- **ScreenshotService.cs** — Crops rendered page PNGs to annotation bounding boxes. Takes `IPdfService` (renders via `RenderPage()` cast to `SkiaRenderedPage`). Groups nearby freehand strokes via a **union-find** algorithm (`MergeDistancePt = 50`). No shell-out.
- **MarkdownBuilder.cs** — Assigns annotations to headings by flattening the PDF outline and matching page ≤ annotation page. Sorts by (page, SortY). Emits: summary table, bold-in-context highlights with 2-tier fuzzy matching (exact → whitespace-collapsed → fallback bold), `CleanText`, blockquoted notes, optional image embeds. Heading depth: 0 → `##`, 1 → `###`, 2+ → `####`.

### Page indexing

Three conventions coexist — be careful which you're using:
- **User input & CLI arguments:** 1-based
- **`AnnotationFile.Pages` keys / internal models:** 0-based
- **Markdown output:** 1-based (`page + 1`)

### AppImage packaging

`build-appimage.sh` publishes a self-contained linux-x64 binary and packages it with `appimagetool`. All files (binary + `.so` libs) stay in `usr/bin/` — the .NET host requires its own libraries (`libhostpolicy.so`, `libcoreclr.so`, etc.) alongside the binary. `AppImage/AppRun` adds `usr/bin/` to `LD_LIBRARY_PATH` so third-party native libs (`libpdfium.so`, `libSkiaSharp.so`, `libonnxruntime.so`) are also found. `$APPDIR` is exported so `LayoutModelLocator` can probe `$APPDIR/models/` for a bundled ONNX model.

## Testing

`InternalsVisibleTo` is enabled so tests can call `internal static MarkdownBuilder.CleanText()`. Tests use inline helpers (`MakeFile()`, `H()`, `Build()`) to construct `AnnotationFile` / `OutlineEntry` objects without the CLI. No integration tests for `ScreenshotService` (requires PDF with renderable pages).
