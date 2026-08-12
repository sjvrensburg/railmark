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
- **Pack (tarball):** `./build-tarball.sh [--include-model <path>]`
- **Fetch layout model:** `./scripts/download-model.sh` → `models/PP-DocLayoutV3.onnx`

Solution file is `RailMark.slnx` (new XML `.slnx` format, not traditional `.sln`).

## Architecture

`RailMark/` is a .NET console app (top-level statements in `Program.cs`), `RailMark.Tests/` is the xUnit test project. All services are static classes — no dependency injection.

### Pipeline (Program.cs)

**Annotation mode (default):**
1. `PdfiumResolver.Initialize()` + `SkiaPdfServiceFactory` init
2. Manual arg parsing → `CompositeAnnotationStore.Default.Load(pdf)` → page/color filter
3. `PdfTextService.ExtractPageText()` per page → `ExtractTextInRect()` per rect of every `TextMarkupAnnotation` (all four subtypes, not just highlights). The probe rect comes from `TextExtractionBounds()`: underline/squiggly rects are only the thin baseline band `MarkupPlanService` writes, so they are grown back to the glyph box (`H / 0.15f`) or they match no characters at all.
4. `ResolveHeadingPositions()` — locates each outline title in its own page's text (`TextLocator.ResolveQuote` → batched `GetTextRangeRects`) so headings can be ordered within a page
5. Optional `ScreenshotService.CropAnnotationsAsync()` for `--images`
6. `MarkdownBuilder.Build()` → write file

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
- **MarkdownBuilder.cs** — Assigns annotations to headings by flattening the PDF outline and taking the last heading preceding the annotation in reading order — `(page, y)`, not page alone, so two headings on one page split their annotations correctly. Heading `y` comes from the optional `headingPositions` ctor argument (keyed by `MarkdownBuilder.HeadingKey`), which `Program.cs` builds via `ResolveHeadingPositions()`; a heading whose title can't be located on its page falls back to `y = 0` and therefore to the old page-level behaviour. Sorts by (page, SortY). Emits: summary table, bold-in-context highlights with 2-tier fuzzy matching (exact → whitespace-collapsed → fallback bold, via `TextLocator`), blockquoted notes, optional image embeds. Heading depth: 0 → `##`, 1 → `###`, 2+ → `####`.
  - All four `TextMarkupAnnotation` subtypes are rendered, not just highlights. Labels carry editorial intent: strikeout → *suggested deletion*, lone caret → *suggested insertion*. A `StrikeOutAnnotation` whose `InReplyTo` names a `CaretAnnotation`'s `NativeId` is folded into that caret as a single *suggested replacement* entry (`BuildCaretReplacements`), and suppressed from its own emission via `_groupChildren`.
  - An annotation whose `/Contents` merely repeats the text it covers (Skim writes the selection into `/Contents`) has the comment dropped — see `IsEchoOfMarkedText`.
- **TextLocator.cs** — Shared text-matching primitive (`NormalizeWithMap`, `CleanText`), extracted from `MarkdownBuilder`. Normalisation collapses whitespace, drops soft hyphens and control characters, expands ligatures and smart quotes via `CharacterSubstitutions`, and rejoins words split across a line break by a wrap hyphen (dropped when the preceding character is lowercase — `com-\npileable`; kept, but with the break swallowed, otherwise — `COVID-\n19`). Because a ligature expands to more characters than it consumes, the index map is built as a `List<int>` and can be longer than the input. `ResolveQuote(pageText, quote)` resolves a quoted string to an exact `(CharStart, CharLength)` range (tier 1 exact match, tier 2 normalised match), returning `null` on no match — used by `MarkupPlanService` as a hard per-entry error, unlike `MarkdownBuilder`'s own silent bold-fallback.
- **MarkupPlanService.cs** — `Resolve(plan, pdf, textService, existing, password)` turns a `MarkupPlan` into an `AnnotationFile` (merged with `existing`, not overwritten) plus a `MarkupPlanApplyResult` of per-entry outcomes. Does not persist — caller calls `CompositeAnnotationStore.Save()`. Note placement: right-margin inset (24pt) at the vertical centre of the quote's last matched line.

### Page indexing

Three conventions coexist — be careful which you're using:
- **User input & CLI arguments:** 1-based
- **`AnnotationFile.Pages` keys / internal models:** 0-based
- **Markdown output:** 1-based (`page + 1`)

`MarkupEntry.Page` in `--apply-markup` plans follows the CLI convention (1-based); `MarkupPlanService.Resolve()` converts to 0-based before touching `AnnotationFile.Pages` or calling `IPdfService`/`IPdfTextService`.

### Packaging

Three release artifacts, all self-contained and all bundling `PP-DocLayoutV3.onnx`:

- **AppImage** (`build-appimage.sh`) — publishes a self-contained linux-x64 binary and packages it with `appimagetool`. All files (binary + `.so` libs) stay in `usr/bin/` — the .NET host requires its own libraries (`libhostpolicy.so`, `libcoreclr.so`, etc.) alongside the binary. `AppImage/AppRun` adds `usr/bin/` to `LD_LIBRARY_PATH` so third-party native libs (`libpdfium.so`, `libSkiaSharp.so`, `libonnxruntime.so`) are also found. `$APPDIR` is exported so `LayoutModelLocator` can probe `$APPDIR/models/` for the bundled model.
- **Tarball** (`build-tarball.sh`) — same binary in a plain directory for hosts without FUSE. Payload lives in `bin/`; a root-level `railmark` launcher reproduces AppRun's `LD_LIBRARY_PATH` + `APPDIR` contract.
- **Windows zip** (CI only) — `dotnet publish -r win-x64 --self-contained`. Natives (`pdfium.dll`, `libSkiaSharp.dll`, `onnxruntime.dll`) resolve from the exe's own directory, so no launcher is needed.

**Model discovery.** `LayoutModelLocator` composes `models` against several base directories. Three are confirmed by experiment: `$APPDIR/models/`, `AppConfig.ConfigDir` (`~/.config/railreader2/models/`), and `<app dir>/models/` — the last is what the Windows package relies on. Note that the second means a developer machine with RailReader2 installed finds a model even when the package under test bundles none; isolate `HOME`/`XDG_CONFIG_HOME` when testing the no-model fallback, or the test silently passes for the wrong reason.

`scripts/download-model.sh` fetches the model and checks it against a pinned SHA-256, so an upstream change fails the build instead of shipping unnoticed. `models/` and `*.onnx` are gitignored.

### CI

- `.github/workflows/ci.yml` — build + test on push/PR, plus a `linux-x64`/`win-x64` publish matrix that asserts the native libraries actually land in the output. Windows packaging is otherwise only exercised at release time.
- `.github/workflows/release.yml` — on a `v*` tag: verify the tag matches `<Version>` in `RailMark.csproj` **and** `VERSION` in `install.sh`, test, build and smoke-test all three artifacts, then open a **draft** release with them plus `SHA256SUMS`. Draft because releases carry hand-written notes. `workflow_dispatch` builds everything without releasing.

Releasing therefore means: bump `RailMark.csproj` and `install.sh` together, merge, tag, then write the notes on the draft and publish.

## Testing

`InternalsVisibleTo` is enabled so tests can call `internal static` members (e.g. `MarkdownBuilder.CleanText()`, `TextLocator.ResolveQuote()`). Tests use inline helpers (`MakeFile()`, `H()`, `Build()`) to construct `AnnotationFile` / `OutlineEntry` objects without the CLI. `MarkupPlanServiceTests.cs` fakes `IPdfService`/`IPdfTextService` directly (no mocking framework anywhere in the repo) to test `MarkupPlanService.Resolve()` without a real PDF.

### Integration checks

Because the xUnit tests fake the PDF services, nothing in them touches real pdfium geometry. `tests/integration-smoke.sh <railmark command...>` covers that gap and runs against any build — `dotnet run`, the AppImage, the tarball launcher, or `railmark.exe`:

```bash
./tests/integration-smoke.sh dotnet run --project RailMark/ --
./tests/integration-smoke.sh ./dist/railmark-0.9.0-linux-x86_64.AppImage
```

It exports the fixture, applies a markup plan, and extracts the annotations back, asserting quote resolution across a wrap hyphen and straight-vs-curly quotes, all four markup subtypes, and heading assignment for two headings on one page. CI runs it in `ci.yml` and against each of the three release artifacts.

`tests/fixtures/sample.pdf` is generated by `tests/fixtures/make-sample-pdf.py` — a dependency-free, byte-deterministic hand-built PDF (CI re-runs the generator and `cmp`s it against the committed file). Edit the generator, never the PDF. Its content is deliberate: two headings on page 1, a word split by a wrap hyphen, and curly quotes.

Two gotchas the fixture exposed, both expected:

- `--export` writes **U+0002** where it rejoined a wrap-hyphenated word, so the output reads as `interpretable` without literally containing that string. `TextLocator` strips U+0002, so a quote copied out of the export still resolves. (Annotation-mode output no longer carries the marker — see `StripInvisibleMarkers` below — but `--export` comes straight from `RailReader.Export` and still does.)
- Marked-up text passes through `CleanText`, so annotation-mode output has straight quotes where the export has curly ones.

### Extracted text is per-line, and the join matters

`PdfTextService.ExtractPageText` **already de-hyphenates**: a word the producer split across a line break comes back as `inter` + **U+0002** + `pretable`, with no hyphen anywhere in the text layer. Two consequences, both handled in `TextLocator`:

- Markup text is extracted one rect per visual line, so the parts must be rejoined through the page text (`SpanCoveringParts`) rather than with a space — a space would render a split word as `inter pretable`. The plain join remains as a fallback when the parts can't be located verbatim.
- Page text emitted verbatim into Markdown (the bold-in-context line) must go through `StripInvisibleMarkers`, which drops U+0002/U+0003/U+00AD while leaving line breaks alone. Apply it *after* any offset arithmetic, since it shifts indices.

Still not covered: `ScreenshotService` (needs renderable pages), and the `--images` path generally.

## Skill for AI-driven markup

`.claude/skills/railmark-markup-plan/` is a project-scoped Claude Code skill teaching an agent the `--apply-markup` workflow: export → draft a JSON markup plan → apply → retry failed quotes. When changing the `MarkupPlan`/`MarkupEntry` schema, the CLI flags, the result-report shape, or the default colours in `MarkupPlanService`, update `.claude/skills/railmark-markup-plan/references/markup-plan-schema.md` and `templates/example-markup-plan.json` to match — they are the skill's only source of truth for the schema and are not derived from the code automatically.
