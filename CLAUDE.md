# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A .NET tool (**RailMark**) that converts PDFs to Markdown suitable for AI summarisation, and writes AI-authored PDF markup back. Three modes: annotation mode extracts annotations from PDFs reviewed in RailReader2; export mode (`--export`) produces layout-aware full-document Markdown from any PDF; apply-markup mode (`--apply-markup`) writes highlight/underline/strikeout/squiggly/replace/note annotations, and replies to existing annotations, into a PDF from a JSON markup plan, so an AI agent can act as a technical editor. Uses the **RailReader.Core NuGet packages** directly — no external CLI required. Distributed as a self-contained AppImage for Linux x86-64.

The `railmark-markup-plan` Claude Code skill (`.claude/skills/railmark-markup-plan/`) teaches an agent the apply-markup workflow — see [Skill for AI-driven markup](#skill-for-ai-driven-markup) below.

## Build & Test

- **Build:** `dotnet build`
- **Test:** `dotnet test`
- **Single test:** `dotnet test --filter "FullyQualifiedName~TestName"`
- **Run:** `dotnet run --project RailMark/ -- <pdf> [-o output.md] [--pages] [--color] [--author] [--exclude-author] [--since] [--format json] [--images] [--export]`
- **Pack (AppImage):** `./build-appimage.sh [--include-model <path>]`
- **Pack (tarball):** `./build-tarball.sh [--include-model <path>]`
- **Fetch layout model:** `./scripts/download-model.sh` → `models/PP-DocLayoutV3.onnx`

Solution file is `RailMark.slnx` (new XML `.slnx` format, not traditional `.sln`).

## Architecture

`RailMark/` is a .NET console app (top-level statements in `Program.cs`), `RailMark.Tests/` is the xUnit test project. All services are static classes — no dependency injection.

### Pipeline (Program.cs)

**Annotation mode (default):**
1. `PdfiumResolver.Initialize()` + `SkiaPdfServiceFactory` init
2. Manual arg parsing → `CompositeAnnotationStore.Default.Load(pdf)` → page/color filter, then `AnnotationFilter.ByAuthor()` / `.Since()` (`--author`, `--exclude-author`, `--since`; `Since` uses `ModifiedUtc ?? CreatedUtc` and drops undated annotations)
3. `PdfTextService.ExtractPageText()` per page → `ExtractTextInRect()` per rect of every `TextMarkupAnnotation` (all four subtypes, not just highlights). The probe rect comes from `TextExtractionBounds()`: underline/squiggly rects are only the thin baseline band `MarkupPlanService` writes, so they are grown back to the glyph box (`H / 0.15f`) or they match no characters at all. The same pass records `markupOffsets` — the lowest `CharBox.Index` inside each markup's first rect — so output can tell *which* occurrence of repeated words was marked.
4. `ResolveHeadingPositions()` — locates each outline title in its own page's text (`TextLocator.ResolveQuote` → batched `GetTextRangeRects`) so headings can be ordered within a page
5. Optional `ScreenshotService.CropAnnotationsAsync()` for `--images`
6. `MarkdownBuilder.Build()`, or `AnnotationJsonBuilder.Build()` with `--format json` → write file. JSON mode writes `{"annotations": []}` rather than nothing when there is nothing to report.

**Export mode (`--export`):**
1. `PdfiumResolver.Initialize()` + `SkiaPdfServiceFactory` init
2. `MarkdownExportService.ExportAsync()` — full layout-aware pipeline with optional VLM

**Apply-markup mode (`--apply-markup <plan.json>`):**
1. `PdfiumResolver.Initialize()` + `SkiaPdfServiceFactory` init
2. Deserialize the JSON plan (`RailMark.Models.MarkupPlan`; `--apply-markup -` reads stdin) → `CompositeAnnotationStore.Default.Load(pdf)` for the existing `AnnotationFile` to merge into
3. `MarkupPlanService.Resolve()` in three passes. **Locate:** `--replace-author` removals first; per entry validate, find the `inReplyTo` parent by `NativeId`, then `TextLocator.FindQuoteMatches()` on a cached `NormalizedText` per page, narrowed by `contextBefore`/`contextAfter` and indexed by `occurrence` (an unqualified multi-match succeeds with a `warning`); a quote not on its page is tried across the break with `FindAcrossPageBreak()`. **Geometry:** every placed part batched into one `GetTextRangeRects()` per page → `MergeLineRects()` (the API returns one rect per character; merges same-line rects by vertical overlap). **Build:** annotations in plan order, each through `Merge()`, which dedupes against what the page already holds (see below)
4. Unless `--dry-run`, `CompositeAnnotationStore.Default.Save(pdf, file)` writes native PDF annotations (falls back to RailReader2 sidecar for signed/locked PDFs)
5. Per-entry results (success/error, `matchCount`, `action`, optional `warning`/`endPage`/`inReplyTo`, and `context` on `--dry-run`; in original plan order) printed as JSON to stdout; human summary, failures and warnings to stderr; exit code `0` all resolved, `2` partial failure, `1` hard error. Warnings don't affect the exit code.

### Models

Annotation types come from `RailReader.Core.Models` via `[JsonPolymorphic]` — no hand-written converter. Key types: `AnnotationFile` (container; `Dictionary<int, List<Annotation>> Pages`, 0-based keys), `HighlightAnnotation`, `UnderlineAnnotation`, `StrikeOutAnnotation`, `SquigglyAnnotation`, `TextNoteAnnotation`, `RectAnnotation`, `FreehandAnnotation`, `CaretAnnotation`, `FreeTextAnnotation`.

Outline from `IPdfService.Outline` → `List<OutlineEntry>` (Title, Page?, Children).

`RailMark.Models.MarkupPlan` (`Models/MarkupPlan.cs`) is RailMark's own DTO for the `--apply-markup` JSON input — not part of RailReader.Core. `MarkupPlan { List<MarkupEntry> Entries }`; `MarkupEntry { Page (1-based), Quote?, Type?, Update?, Comment?, Color?, Author?, Opacity?, Occurrence?, ContextBefore?, ContextAfter?, InReplyTo?, Replacement? }`; `MarkupType` enum (`Highlight`/`Underline`/`Strikeout`/`Squiggly`/`Note`/`Replace`). `Type` is nullable only so an `Update` entry may omit it (`Kind` is the validated value). `Quote` is optional only for a `Note` with `InReplyTo`, or an `Update`; `MarkupPlanService.Validate()` enforces the per-type rules, since `required` can't express them. See `.claude/skills/railmark-markup-plan/references/markup-plan-schema.md` for the full field reference.

### Services

- **ScreenshotService.cs** — Crops rendered page PNGs to annotation bounding boxes. Takes `IPdfService` (renders via `RenderPage()` cast to `SkiaRenderedPage`). Groups nearby freehand strokes via a **union-find** algorithm (`MergeDistancePt = 50`). No shell-out.
- **MarkdownBuilder.cs** — Assigns annotations to headings by flattening the PDF outline and taking the last heading preceding the annotation in reading order — `(page, y)`, not page alone, so two headings on one page split their annotations correctly. The heading logic lives in `HeadingIndex`. Heading `y` comes from the optional `headingPositions` ctor argument (keyed by `HeadingIndex.Key`), which `Program.cs` builds via `ResolveHeadingPositions()`; a heading whose title can't be located on its page falls back to `y = 0` and therefore to the old page-level behaviour. Sorts by (page, SortY). Emits: summary table, bold-in-context highlights (normalised match via `TextLocator.FindQuoteMatches`, choosing the match nearest the annotation's `markupOffsets` entry so repeated words bold the right occurrence; fallback: bold text alone), blockquoted notes, optional image embeds. Heading depth: 0 → `##`, 1 → `###`, 2+ → `####`.
  - All four `TextMarkupAnnotation` subtypes are rendered, not just highlights. Labels carry editorial intent: strikeout → *suggested deletion*, lone caret → *suggested insertion*. A `StrikeOutAnnotation` whose `InReplyTo` names a `CaretAnnotation`'s `NativeId` is folded into that caret as a single *suggested replacement* entry (`BuildCaretReplacements`), and suppressed from its own emission via `_groupChildren`.
  - Also rendered as single entries: a RailMark replacement strikeout (`ReplacementMarkup`), and a markup split over a page break (`ContinuationMarkup` — the tail joins `_groupChildren`, the head is labelled `pp. N–N+1`). Reply notes (`ReplyMarkup`) are threaded under their parent.
  - An annotation whose `/Contents` merely repeats the text it covers (Skim writes the selection into `/Contents`) has the comment dropped — see `IsEchoOfMarkedText`.
- **TextLocator.cs** — Shared text-matching primitive (`NormalizeWithMap`, `CleanText`), extracted from `MarkdownBuilder`. Normalisation collapses whitespace, drops soft hyphens and control characters, expands ligatures and smart quotes via `CharacterSubstitutions`, and rejoins words split across a line break by a wrap hyphen (dropped when the preceding character is lowercase — `com-\npileable`; kept, but with the break swallowed, otherwise — `COVID-\n19`). Because a ligature expands to more characters than it consumes, the index map is built as a `List<int>` and can be longer than the input. `ResolveQuote(pageText, quote)` returns the first match's `(CharStart, CharLength)` or `null` — now only used for heading positions.
  - `FindQuoteMatches(NormalizedText, quote)` returns *every* match, searched in normalised space (a superset of verbatim matching, so the count is honest). `NormalizedText.MatchAt()` maps back to the original with the end taken one past the last matched char, so trailing dropped markers aren't given geometry. `HasContext`, `DescribeContext` and `FindAcrossPageBreak` build on it. A page-break split must fall at a line break in the *original* text (head ends a line, tail starts one; a mid-word split needs a real hyphen there), each half ≥ `MinSplitPart` (3) chars and within `MaxPageBreakGap` (300 normalised chars) of the break. Proximity alone let a wrong-page quote "resolve" as a stray letter plus the rest on the next page; a quote not found but present on a neighbouring page instead fails naming that page.
- **MarkupPlanService.cs** — `Resolve(plan, pdf, textService, existing, password, includeContext, replaceAuthor)` turns a `MarkupPlan` into an `AnnotationFile` (merged with `existing`, not overwritten) plus a `MarkupPlanApplyResult` of per-entry outcomes and a `Removed` count. Does not persist — caller calls `CompositeAnnotationStore.Save()`. Note placement is `NotePlacement`'s job (see below).
  - **Dedupe** (`Merge`/`IsSameMarkup`): same subtype + author + parent + geometry within `SameGeometryTolerance` (1.5pt — geometry read back from a PDF isn't bit-identical) is the same annotation; differing comment/colour updates it in place. Replacement vs plain strikeout, and continuation vs not, are distinct. **Notes** are matched on text plus position: a note is built at its *ideal* spot and only moved (by `NotePlacement`) once Merge knows it is new, so a note read back from the PDF counts as the same note when its text matches and it lies within `NotePlacement.MaxDrift` of the ideal; a note placed earlier *in the same run* must have been aimed at the same ideal spot (`RunState.Ideals`). Of several candidates the nearest wins, preferring one no earlier entry of the run has claimed (`RunState.Claimed`), so two same-text notes on neighbouring lines each find their own. **Reply notes** are matched on parent + author + text alone. A twin that an *earlier entry of the same run* put in place (`Touched`) and that the current entry would change is a conflict: the later entry fails rather than overwriting.
- **NotePlacement.cs** — where a sticky note's icon goes. PDFium draws a text annotation as a **24pt square whose top-left is X/Y** (measured on the fixture: a note at (500, 100) is written as `/Rect [500 692 524 668]`), so the icon has to be put somewhere rather than drawn over its text. `Ideal(anchorLine, chars, page)`: the icon is centred on the quote's last line and sits `Gap` (4pt) outside the **text column** that line belongs to — found from the page's `CharBox`es within ±4 line heights of the anchor, split into columns at horizontal gaps wider than 1.5 line heights — on whichever side has room nearer (right wins a tie, by `SidePreference`, so equal margins don't flip from one line to the next). A two-column page therefore puts a left-column note in the left margin or the gutter. The chosen side is walked outward past any glyphs the icon would cover and clamped inside the page; with no `CharBox`es (the unit-test fakes) the quote's own rect stands in for the column. `Beside(...)` then avoids the icons already on the page (`occupied`): it walks down past each blocker to `blocker.Bottom + Gap`, then up, never further than `MaxDrift` (6 icons) from the ideal, and uses the ideal regardless when nothing is free. A **reply** does not walk: it takes the spot `UnderThread` — directly beneath the lowest icon of its parent's thread (the parent note plus its existing reply notes), or the `Ideal` spot beside a text-markup parent's last line when the thread has no icon yet — and `Insert` pushes every icon it would overlap down to just beneath it, cascading, so a thread stays contiguous and the siblings move. Pushing moves existing notes, which dedupe tolerates up to `MaxDrift`: a plain note with more than six replies inserted above it is no longer recognised on re-apply. Both use `CharBoxes` from the same `ExtractPageText` call the quote was located in, cached per page in `Resolve`.
  - **Opacity** (`Annotation.Opacity`, written by the PDF writer as `/CA`; `MarkupEntry.Opacity`, `--opacity` → `Resolve(defaultOpacity)`): entry, else the run default, else `DefaultHighlightOpacity` (0.35, RailReader2's own highlight default) for a highlight and 1 for the other text markup; notes ignore it. Only an *explicit* opacity (entry or `--opacity`, tracked in `RunState.ExplicitOpacity`) takes part in dedupe and is written over a twin, so re-applying an old plan doesn't repaint existing highlights; comparison uses `SameOpacityTolerance` (0.01) because `/CA` is an 8-bit alpha. `AnnotationRecord.Opacity` is rounded to 2 places. `/BM /Multiply` is out of scope (PDFium has no appearance-stream API).
  - **Near-duplicate warning** (`FindNearDuplicate`): an entry that `Merge` *adds* gets a `Near-duplicate of <id>` warning when a pre-existing text markup of the same subtype (and replacement-ness; not a reply or continuation) on the page overlaps it by ≥ `NearDuplicateOverlap` (0.5) intersection-over-union (so a short markup nested in a long one is not flagged); only the first part of a page-break markup is checked — any author, any line split. Annotations added earlier in the same run never count. Warning only; the exit code is unaffected.
  - **Update by id** (`MarkupEntry.Update`, `ApplyUpdate`): bypasses quote location and geometry entirely. Pass 1 finds the target by `NativeId` on the entry's page; pass 3 patches comment/colour/author on the live annotation (a replacement strikeout keeps its `Replace with:` prefix via `ReplacementMarkup`), so id, `/CreationDate` and position survive. Two updates of one annotation in a run conflict via `RunState.Touched`. A colour/author change also reaches the `#cont-` tail of a page-break markup. An annotation with no `/NM` has no id and can't be targeted. Renaming the author changes the dedupe key (author is part of it), so a plan still naming the old author re-adds rather than matches; removing a parent takes its replies with it. `MarkupEntry.Kind` throws when `Type` is null.
  - `--replace-author`, `--replace-unauthored` (also `--replace-author ""`) and `--remove-ids` all go through `Remove`, which also drops the `#cont-` tail of a removed head and every reply beneath a removed annotation (transitively, on any page, however it was linked) and reports unmatched ids in `MissingIds`. It leaves emptied pages as empty lists, **not** removed keys: `PdfAnnotationWriter.WriteReconciled` only visits pages present in the file, so a dropped key would leave the old annotations in the PDF.
- **HeadingIndex.cs** — outline order plus (page, y) reading order, extracted from `MarkdownBuilder` so both output formats file annotations under the same heading. `HeadingIndex.Key` is the key `headingPositions` uses (`MarkdownBuilder.HeadingKey` forwards to it).
- **AnnotationJsonBuilder.cs** — `--format json`. One `AnnotationRecord` per annotation in reading order; field names deliberately match `MarkupEntry` so records re-apply as a plan. `occurrence` is filled only when the quote repeats on the page (resolved via `markupOffsets`); continuations fold into their head with `endPage`.
- **AnnotationFilter.cs** — `Where`, `ByAuthor` (case-insensitive; anonymous annotations never match an include list and are never excluded), `Since`.

#### Conventions forced by PDFium

PDFium can neither **create a caret** nor natively **write `/IRT`** (Core 0.71 adds `/IRT` through its own raw-PDF post-pass, but not for every file — see `ReplyMarkup`), so three RailMark conventions stand in for the native structures. Each lives in its own small class, which both writers and readers go through:

- **ReplacementMarkup.cs** — a `replace` entry is one `StrikeOutAnnotation` with `/Subj "Replacement"` and `/Contents "Replace with: <text>"` (+ `"\n\n" + comment`), instead of Acrobat's caret + strikeout pair. `TryParse` recognises it.
- **ContinuationMarkup.cs** — a quote over a page break becomes two annotations; the second has `/Subj "Continuation"`, no contents, and `/NM` `<head /NM>#cont-<guid>` (the head gets a preset `/NM` so the link can be made before saving). `Pair()` matches each to its head by that link, falling back to position (previous page, same subtype and author, bottom-most), and both extractors fold it in, so it reports as one markup spanning pp. N–N+1. `IdSuffix.Prefix` only reads the *last* id segment, so a reply's continuation (`p#reply-x#cont-y`) isn't mistaken for a reply to `p`.
- **ReplyMarkup.cs** — a reply's `/NM` is `<parent /NM>#reply-<guid>`, set on the model's `NativeId` before saving (the reconciler writes a preset `/NM` as-is). `ParentOf()` prefers a real `/IRT` (`InReplyTo`), else parses the id. Since Core 0.71 `WriteReconciled` also writes `/IRT` from `InReplyTo` as an incremental update (so the raw file holds two revisions of each linked annotation); it is skipped for signed/read-only files (sidecar) and RC4/AES-128 encryption, which is why the `/NM` link stays. `MarkdownBuilder` threads reply *notes* under their parent as nested blockquotes; `BuildReplyThreads` takes its cycle-safe membership from Core's `AnnotationThreads.Build` (which reads `InReplyTo` only, so `ReplyMarkup.LinkReplies` first copies the `/NM` convention into `InReplyTo`): a loop of replies (malformed `/IRT`) is cut at its earliest member, which stays a plain note while the rest nest beneath it. Nesting itself still follows `InReplyTo`, not Core's flat thread order. `--apply-markup` also calls `LinkReplies` on load, so saving writes the missing `/IRT` for older RailMark replies.

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
./tests/integration-smoke.sh ./dist/railmark-<version>-linux-x86_64.AppImage
```

It exports the fixture, applies a markup plan, and extracts the annotations back, asserting quote resolution across a wrap hyphen and straight-vs-curly quotes, all four markup subtypes, and heading assignment for two headings on one page. Later sections cover occurrence/context selection, `replace`, idempotent re-apply and `--replace-author`, `--format json` (including feeding extracted records back in as a plan), quotes across a page break, replies, stdin plans and the author/date filters — each against real pdfium geometry and a real write/read, which is where tolerances and the `/Subj`/`/NM` conventions are actually proven. It needs `jq` (present on the GitHub runners). CI runs it in `ci.yml` and against each of the three release artifacts.

`tests/fixtures/sample.pdf` is generated by `tests/fixtures/make-sample-pdf.py` — a dependency-free, byte-deterministic hand-built PDF (CI re-runs the generator and `cmp`s it against the committed file). Edit the generator, never the PDF. Its content is deliberate: two headings on page 1, a word split by a wrap hyphen, curly quotes, a phrase repeated on page 2, and a sentence that runs from page 2 — past a "Page 2" footer — onto page 3.

A gotcha the fixture exposed: marked-up text passes through `CleanText`, so annotation-mode output has straight quotes where the export has curly ones.

`--export` used to write **U+0002** where it rejoined a wrap-hyphenated word (RailReaderCore#101), fixed in RailReader.Core **0.56.1**. The integration checks now assert the export contains no invisible markers, so a regression there is caught rather than worked around.

### Extracted text is per-line, and the join matters

`PdfTextService.ExtractPageText` **already de-hyphenates**: a word the producer split across a line break comes back as `inter` + **U+0002** + `pretable`, with no hyphen anywhere in the text layer. This is still true in 0.56.1 — the marker is deliberate on the *extraction* API, and only the Markdown renderer stopped leaking it. Two consequences, both handled in `TextLocator`:

- Markup text is extracted one rect per visual line, so the parts must be rejoined through the page text (`SpanCoveringParts`) rather than with a space — a space would render a split word as `inter pretable`. The plain join remains as a fallback when the parts can't be located verbatim.
- Page text emitted verbatim into Markdown (the bold-in-context line) must go through `StripInvisibleMarkers`, which drops U+0002/U+0003/U+00AD while leaving line breaks alone. Apply it *after* any offset arithmetic, since it shifts indices.

`ScreenshotServiceTests.cs` covers the `--images` path without pdfium. The geometry helpers are `internal static` so they can be tested directly, and `CropAnnotationsAsync` is driven through a `FakePdfService` that returns a synthetic `SkiaRenderedPage` (its constructor takes an `SKBitmap`) painted red across its top eighth — so a sampled pixel proves *where* on the page a crop came from.

### Annotation coordinates are top-down

`CompositeAnnotationStore` converts out of the PDF's bottom-up space when it loads, for both `RectAnnotation.X/Y` and `FreehandAnnotation.Points` (verified for each: a `/Rect [72 690 300 730]` on a 792pt page loads as `Y=62`, and an `/InkList` point at `y=600` loads as `192`). `ScreenshotService.ToPixelRect` therefore scales without flipping. It used to flip, which mirrored every crop vertically — issue #22, invisible unless the annotation sat away from the page's vertical centre.

Note also that the renderer paints **page content only**: annotation appearances are not drawn into the bitmap. A crop shows the text under the annotation, never the annotation itself — which is why the fixture's ink strokes are positioned over body text, so "the crop is not blank" is a meaningful assertion.

Still not covered: `RenderPage` itself, and the `SkiaRenderedPage` cast in `CropAnnotationsAsync` — a non-Skia renderer silently yields no images.

## Skill for AI-driven markup

`.claude/skills/railmark-markup-plan/` is a project-scoped Claude Code skill teaching an agent the `--apply-markup` workflow: export → draft a JSON markup plan → apply → retry failed quotes. When changing the `MarkupPlan`/`MarkupEntry` schema, the CLI flags, the result-report shape, or the default colours in `MarkupPlanService`, update `.claude/skills/railmark-markup-plan/references/markup-plan-schema.md` and `templates/example-markup-plan.json` to match — they are the skill's only source of truth for the schema and are not derived from the code automatically. The skill's Setup section also hard-codes the minimum `railmark` version (`need=`); raise it when the skill starts documenting a feature that only newer builds have.
