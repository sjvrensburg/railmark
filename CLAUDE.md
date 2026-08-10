# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A .NET tool (**RailMark**) that converts PDFs to Markdown suitable for AI summarisation, and writes AI-authored PDF markup back. Three modes: annotation mode extracts annotations from PDFs reviewed in RailReader2; export mode (`--export`) produces layout-aware full-document Markdown from any PDF; apply-markup mode (`--apply-markup`) writes highlight/underline/strikeout/squiggly/note annotations into a PDF from a JSON markup plan, so an AI agent can act as a technical editor. Uses the **RailReader.Core NuGet packages** directly — no external CLI required. Distributed as a self-contained AppImage for Linux x86-64.

The `railmark-markup-plan` Claude Code skill (`.claude/skills/railmark-markup-plan/`) teaches an agent the apply-markup workflow — see [Skill for AI-driven markup](#skill-for-ai-driven-markup) below.

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

**Apply-markup mode (`--apply-markup <plan.json>`):**
1. `PdfiumResolver.Initialize()` + `SkiaPdfServiceFactory` init
2. Deserialize the JSON plan (`RailMark.Models.MarkupPlan`) → `CompositeAnnotationStore.Default.Load(pdf)` for the existing `AnnotationFile` to merge into
3. `MarkupPlanService.Resolve()` — per page: `PdfTextService.ExtractPageText()` once, `TextLocator.ResolveQuote()` per entry (quote → char range), batched `GetTextRangeRects()` per page → `MergeLineRects()` (the API returns one rect per character; merges same-line rects by vertical overlap) → builds the matching `Annotation` subtype
4. Unless `--dry-run`, `CompositeAnnotationStore.Default.Save(pdf, file)` writes native PDF annotations (falls back to RailReader2 sidecar for signed/locked PDFs)
5. Per-entry results (success/error, in original plan order) printed as JSON to stdout; human summary to stderr; exit code `0` all resolved, `2` partial failure, `1` hard error

### Models

Annotation types come from `RailReader.Core.Models` via `[JsonPolymorphic]` — no hand-written converter. Key types: `AnnotationFile` (container; `Dictionary<int, List<Annotation>> Pages`, 0-based keys), `HighlightAnnotation`, `UnderlineAnnotation`, `StrikeOutAnnotation`, `SquigglyAnnotation`, `TextNoteAnnotation`, `RectAnnotation`, `FreehandAnnotation`, `CaretAnnotation`, `FreeTextAnnotation`.

Outline from `IPdfService.Outline` → `List<OutlineEntry>` (Title, Page?, Children).

`RailMark.Models.MarkupPlan` (`Models/MarkupPlan.cs`) is RailMark's own DTO for the `--apply-markup` JSON input — not part of RailReader.Core. `MarkupPlan { List<MarkupEntry> Entries }`; `MarkupEntry { Page (1-based), Quote, Type, Comment?, Color?, Author? }`; `MarkupType` enum (`Highlight`/`Underline`/`Strikeout`/`Squiggly`/`Note`). See `.claude/skills/railmark-markup-plan/references/markup-plan-schema.md` for the full field reference.

### Services

- **ScreenshotService.cs** — Crops rendered page PNGs to annotation bounding boxes. Takes `IPdfService` (renders via `RenderPage()` cast to `SkiaRenderedPage`). Groups nearby freehand strokes via a **union-find** algorithm (`MergeDistancePt = 50`). No shell-out.
- **MarkdownBuilder.cs** — Assigns annotations to headings by flattening the PDF outline and matching page ≤ annotation page. Sorts by (page, SortY). Emits: summary table, bold-in-context highlights with 2-tier fuzzy matching (exact → whitespace-collapsed → fallback bold, via `TextLocator`), blockquoted notes, optional image embeds. Heading depth: 0 → `##`, 1 → `###`, 2+ → `####`.
- **TextLocator.cs** — Shared whitespace-tolerant text-matching primitive (`NormalizeWithMap`, `CleanText`), extracted from `MarkdownBuilder`. `ResolveQuote(pageText, quote)` resolves a quoted string to an exact `(CharStart, CharLength)` range (tier 1 exact match, tier 2 whitespace-normalised match), returning `null` on no match — used by `MarkupPlanService` as a hard per-entry error, unlike `MarkdownBuilder`'s own silent bold-fallback.
- **MarkupPlanService.cs** — `Resolve(plan, pdf, textService, existing, password)` turns a `MarkupPlan` into an `AnnotationFile` (merged with `existing`, not overwritten) plus a `MarkupPlanApplyResult` of per-entry outcomes. Does not persist — caller calls `CompositeAnnotationStore.Save()`. Note placement: right-margin inset (24pt) at the vertical centre of the quote's last matched line.

### Page indexing

Three conventions coexist — be careful which you're using:
- **User input & CLI arguments:** 1-based
- **`AnnotationFile.Pages` keys / internal models:** 0-based
- **Markdown output:** 1-based (`page + 1`)

`MarkupEntry.Page` in `--apply-markup` plans follows the CLI convention (1-based); `MarkupPlanService.Resolve()` converts to 0-based before touching `AnnotationFile.Pages` or calling `IPdfService`/`IPdfTextService`.

### AppImage packaging

`build-appimage.sh` publishes a self-contained linux-x64 binary and packages it with `appimagetool`. All files (binary + `.so` libs) stay in `usr/bin/` — the .NET host requires its own libraries (`libhostpolicy.so`, `libcoreclr.so`, etc.) alongside the binary. `AppImage/AppRun` adds `usr/bin/` to `LD_LIBRARY_PATH` so third-party native libs (`libpdfium.so`, `libSkiaSharp.so`, `libonnxruntime.so`) are also found. `$APPDIR` is exported so `LayoutModelLocator` can probe `$APPDIR/models/` for a bundled ONNX model.

## Testing

`InternalsVisibleTo` is enabled so tests can call `internal static` members (e.g. `MarkdownBuilder.CleanText()`, `TextLocator.ResolveQuote()`). Tests use inline helpers (`MakeFile()`, `H()`, `Build()`) to construct `AnnotationFile` / `OutlineEntry` objects without the CLI. `MarkupPlanServiceTests.cs` fakes `IPdfService`/`IPdfTextService` directly (no mocking framework anywhere in the repo) to test `MarkupPlanService.Resolve()` without a real PDF. No integration tests for `ScreenshotService` or for writing/reading real PDF annotations (require a PDF with renderable pages / real pdfium round-trip) — verify those manually against a real PDF.

## Skill for AI-driven markup

`.claude/skills/railmark-markup-plan/` is a project-scoped Claude Code skill teaching an agent the `--apply-markup` workflow: export → draft a JSON markup plan → apply → retry failed quotes. When changing the `MarkupPlan`/`MarkupEntry` schema, the CLI flags, the result-report shape, or the default colours in `MarkupPlanService`, update `.claude/skills/railmark-markup-plan/references/markup-plan-schema.md` and `templates/example-markup-plan.json` to match — they are the skill's only source of truth for the schema and are not derived from the code automatically.
