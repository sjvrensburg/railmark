using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;
using RailReader.Renderer.Skia;
using RailMark.Models;
using RailMark.Services;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
    return 0;
}

if (args.Contains("--version"))
{
    Console.WriteLine($"railmark {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}");
    return 0;
}

// Parse arguments
string? pdfPath = null;
string? outputPath = null;
bool includeImages = false;
string? pagesArg = null;
string? colorArg = null;
bool exportMode = false;
bool noVlm = false;
string? vlmEndpoint = null;
string? vlmModel = null;
string? vlmApiKey = null;
string? markupPlanPath = null;
bool dryRun = false;
bool inPlace = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--images":
            includeImages = true;
            break;
        case "--pages" when i + 1 < args.Length:
            pagesArg = args[++i];
            break;
        case "--color" when i + 1 < args.Length:
            colorArg = args[++i];
            break;
        case "--export":
            exportMode = true;
            break;
        case "--no-vlm":
            noVlm = true;
            break;
        case "--vlm-endpoint" when i + 1 < args.Length:
            vlmEndpoint = args[++i];
            break;
        case "--vlm-model" when i + 1 < args.Length:
            vlmModel = args[++i];
            break;
        case "--vlm-api-key" when i + 1 < args.Length:
            vlmApiKey = args[++i];
            break;
        case "--apply-markup" when i + 1 < args.Length:
            markupPlanPath = args[++i];
            break;
        case "--dry-run":
            dryRun = true;
            break;
        case "--in-place":
            inPlace = true;
            break;
        default:
            if (!args[i].StartsWith('-'))
                pdfPath = args[i];
            break;
    }
}

if (string.IsNullOrWhiteSpace(pdfPath))
{
    Console.Error.WriteLine("Error: No PDF path specified.");
    PrintUsage();
    return 1;
}

if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine($"Error: File not found: {pdfPath}");
    return 1;
}

bool stdoutMode = outputPath == "-";
if (stdoutMode && includeImages)
{
    Console.Error.WriteLine("Error: --images is not compatible with -o - (stdout output).");
    return 1;
}

if (exportMode && (colorArg != null || includeImages))
{
    Console.Error.WriteLine("Error: --export cannot be combined with --color or --images.");
    return 1;
}

if (markupPlanPath != null && (exportMode || colorArg != null || includeImages || pagesArg != null))
{
    Console.Error.WriteLine("Error: --apply-markup cannot be combined with --export, --color, --images, or --pages.");
    return 1;
}

if (dryRun && markupPlanPath == null)
{
    Console.Error.WriteLine("Error: --dry-run is only valid with --apply-markup.");
    return 1;
}

if (inPlace && markupPlanPath == null)
{
    Console.Error.WriteLine("Error: --in-place is only valid with --apply-markup.");
    return 1;
}

if (inPlace && outputPath != null)
{
    Console.Error.WriteLine("Error: --in-place cannot be combined with -o.");
    return 1;
}

HashSet<string>? colorFilter = null;
if (colorArg != null)
{
    colorFilter = ParseColorFilter(colorArg);
    if (colorFilter == null || colorFilter.Count == 0)
    {
        Console.Error.WriteLine($"Error: Invalid colour filter: {colorArg}");
        return 1;
    }
}

if (!stdoutMode && markupPlanPath == null)
    outputPath ??= exportMode
        ? Path.ChangeExtension(pdfPath, null) + "-export.md"
        : Path.ChangeExtension(pdfPath, null) + "-annotations.md";

// Initialise PDFium native library
PdfiumResolver.Initialize();
var factory = new SkiaPdfServiceFactory();

// --- Apply-markup mode: write AI-authored PDF markup from a JSON plan ---
if (markupPlanPath != null)
{
    if (!File.Exists(markupPlanPath))
    {
        Console.Error.WriteLine($"Error: Markup plan not found: {markupPlanPath}");
        return 1;
    }

    MarkupPlan plan;
    try
    {
        var planJson = await File.ReadAllTextAsync(markupPlanPath);
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        plan = JsonSerializer.Deserialize<MarkupPlan>(planJson, jsonOptions)
            ?? throw new JsonException("Plan deserialized to null.");
    }
    catch (JsonException ex)
    {
        Console.Error.WriteLine($"Error: Invalid markup plan JSON: {ex.Message}");
        return 1;
    }

    if (outputPath == "-")
    {
        Console.Error.WriteLine("Error: -o - (stdout) is not supported with --apply-markup; PDF output must be a file.");
        return 1;
    }

    string markupOutputPath = inPlace
        ? pdfPath
        : outputPath ?? Path.ChangeExtension(pdfPath, null) + "-marked.pdf";

    var markupPdf = factory.CreatePdfService(pdfPath);
    var markupTextService = new PdfTextService();

    AnnotationFile? existing;
    try
    {
        existing = CompositeAnnotationStore.Default.Load(pdfPath);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: Failed to load existing annotations: {ex.Message}");
        return 1;
    }

    var (annotationFileToSave, applyResult) = MarkupPlanService.Resolve(plan, markupPdf, markupTextService, existing);

    var incomingAuthors = plan.Entries
        .Select(e => e.Author ?? "AI Reviewer")
        .ToHashSet();
    bool authorCollision = existing?.Pages.Values
        .SelectMany(a => a)
        .Any(a => a.Author != null && incomingAuthors.Contains(a.Author)) ?? false;
    if (authorCollision)
    {
        Console.Error.WriteLine(
            "Warning: the target already contains annotations by an author used in this plan — " +
            "this may be a repeat run duplicating earlier markup.");
    }

    if (!dryRun)
    {
        if (markupOutputPath != pdfPath)
            File.Copy(pdfPath, markupOutputPath, overwrite: true);

        CompositeAnnotationStore.Default.OnSidecarFallback = (path, reason) =>
            Console.Error.WriteLine($"Warning: {path} — falling back to sidecar storage ({reason}).");

        bool saved;
        try
        {
            saved = CompositeAnnotationStore.Default.Save(markupOutputPath, annotationFileToSave);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to write annotations: {ex.Message}");
            return 1;
        }

        if (!saved)
        {
            Console.Error.WriteLine("Error: Failed to write annotations.");
            return 1;
        }

        Console.Error.WriteLine($"Written to: {markupOutputPath}");
    }

    var reportOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
    Console.Write(JsonSerializer.Serialize(applyResult.Entries, reportOptions));

    int succeeded = applyResult.Entries.Count(e => e.Success);
    Console.Error.WriteLine(
        $"Applied {succeeded}/{applyResult.Entries.Count} markup entries" + (dryRun ? " (dry run)." : "."));
    foreach (var failed in applyResult.Entries.Where(e => !e.Success))
    {
        var quote = failed.Quote.Length > 60 ? failed.Quote[..60] + "…" : failed.Quote;
        Console.Error.WriteLine($"  FAILED page {failed.Page}: \"{quote}\" — {failed.Error}");
    }

    return applyResult.AllSucceeded ? 0 : 2;
}

// --- Export mode: delegate entirely to RailReader.Export pipeline ---
if (exportMode)
{
    Console.Error.WriteLine($"Exporting: {Path.GetFileName(pdfPath)}");

    VlmEndpointConfig? vlmConfig = null;
    if (!noVlm && vlmEndpoint != null && vlmModel != null)
        vlmConfig = new VlmEndpointConfig(vlmEndpoint, vlmModel, vlmApiKey);

    var exportOptions = new MarkdownExportOptions
    {
        EnableVlm = !noVlm,
        IncludeAnnotations = true,
        InsertPageBreaks = true,
        PageRange = pagesArg,
        VlmEndpoint = vlmConfig,
    };

    var exporter = new MarkdownExportService(factory);

    try
    {
        if (stdoutMode)
        {
            await exporter.ExportAsync(pdfPath, Console.Out, exportOptions,
                progress: new Progress<ExportProgress>(p => Console.Error.WriteLine($"  {p.Status}")));
        }
        else
        {
            using var sw = new StreamWriter(outputPath!, append: false, System.Text.Encoding.UTF8);
            await exporter.ExportAsync(pdfPath, sw, exportOptions,
                progress: new Progress<ExportProgress>(p => Console.Error.WriteLine($"  {p.Status}")));
            Console.Error.WriteLine($"Written to: {outputPath}");
        }
    }
    catch (Exception ex)
    {
        // Per-page failures are already caught and inlined by MarkdownExportService, so
        // reaching here means the export aborted outright (e.g. bad --pages range) rather than
        // silently truncating — surface it clearly instead of a raw stack trace and a
        // misleading exit code 0.
        Console.Error.WriteLine($"Error: Export failed: {ex.Message}");
        return 1;
    }
    return 0;
}

// --- Annotations mode ---
Console.Error.WriteLine($"Extracting annotations from: {Path.GetFileName(pdfPath)}");

AnnotationFile? annotationFile;
try
{
    annotationFile = CompositeAnnotationStore.Default.Load(pdfPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: Failed to load annotations: {ex.Message}");
    return 1;
}

if (annotationFile == null || !annotationFile.Pages.Any(p => p.Value.Count > 0))
{
    Console.Error.WriteLine("No annotations found. Nothing to export.");
    return 0;
}

// Apply page range filter (CLI pages are 1-based; AnnotationFile keys are 0-based)
if (pagesArg != null)
{
    int maxPage = annotationFile.Pages.Keys.Max() + 2;
    var allowed = new HashSet<int>(ParsePageRange(pagesArg, maxPage).Select(p => p - 1));
    annotationFile = FilterPages(annotationFile, (pageIdx, _) => allowed.Contains(pageIdx));
}

// Apply colour filter
if (colorFilter != null)
    annotationFile = FilterAnnotations(annotationFile, a => colorFilter.Contains(NormalizeColor(a.Color)));

int totalAnnotations = annotationFile.Pages.Values.Sum(p => p.Count);
Console.Error.WriteLine($"Found {totalAnnotations} annotations across {annotationFile.Pages.Count} pages.");

if (totalAnnotations == 0)
{
    Console.Error.WriteLine("No annotations found after filtering. Nothing to export.");
    return 0;
}

var pdf = factory.CreatePdfService(pdfPath);
var textService = new PdfTextService();

// Pre-extract per-page text and marked-up text from PDF character boxes. Every text-markup
// subtype (highlight, underline, strikeout, squiggly) covers text worth quoting, not just
// highlights.
var pageTexts = new Dictionary<int, string>();
var highlightTexts = new Dictionary<(int page, int annotIdx), string>();

foreach (var (pageIdx, annotations) in annotationFile.Pages)
{
    PageText? pageText = null;
    try
    {
        pageText = textService.ExtractPageText(pdf.PdfBytes, pageIdx);
        if (!string.IsNullOrEmpty(pageText.Text))
            pageTexts[pageIdx] = pageText.Text;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warning: Could not extract text from page {pageIdx + 1}: {ex.Message}");
    }

    for (int i = 0; i < annotations.Count; i++)
    {
        if (pageText != null && annotations[i] is TextMarkupAnnotation markup && markup.Rects.Count > 0)
        {
            var parts = markup.Rects
                .Select(r =>
                {
                    var (x0, y0, x1, y1) = TextExtractionBounds(markup, r);
                    return pageText.ExtractTextInRect(x0, y0, x1, y1);
                })
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            if (parts.Count > 0)
                highlightTexts[(pageIdx, i)] = string.Join(" ", parts);
        }
    }
}

var headingPositions = ResolveHeadingPositions(pdf, textService, pdf.Outline, pageTexts);

// Optionally render and crop screenshots
Dictionary<(int page, int annotIdx), string>? images = null;
string? imageRelDir = null;

if (includeImages)
{
    var imageDir = Path.ChangeExtension(outputPath, null) + "-images";
    imageRelDir = Path.GetFileName(imageDir);
    Console.Error.WriteLine("Rendering screenshots...");
    images = await ScreenshotService.CropAnnotationsAsync(pdf, annotationFile, imageDir);
    Console.Error.WriteLine($"Cropped {images.Count} annotation images.");
}

var builder = new MarkdownBuilder(
    annotationFile,
    Path.GetFileName(pdfPath),
    pdf.Outline,
    highlightTexts,
    pageTexts,
    images,
    imageRelDir,
    headingPositions);

var markdown = builder.Build();

if (stdoutMode)
    Console.Write(markdown);
else
{
    File.WriteAllText(outputPath!, markdown);
    Console.Error.WriteLine($"Written to: {outputPath}");
}

return 0;

// --- Helpers ---

/// <summary>
/// The rect to pull text from for a text-markup annotation. Highlights and strikeouts are drawn
/// across the whole glyph box, so their rect is the text. Underline and squiggly rects are only a
/// thin band at the baseline (see MarkupPlanService), and extracting from that band finds no
/// characters at all — so grow it back up to the glyph box it was derived from.
/// </summary>
static (float X0, float Y0, float X1, float Y1) TextExtractionBounds(TextMarkupAnnotation markup, HighlightRect r)
{
    if (markup is not (UnderlineAnnotation or SquigglyAnnotation))
        return (r.X, r.Y, r.X + r.W, r.Y + r.H);

    // MarkupPlanService builds the band as 15% of the glyph height (floored at 2pt), so the
    // inverse recovers the glyph box, and never shrinks it when the floor was applied.
    var glyphHeight = MathF.Max(r.H / 0.15f, r.H);
    var bottom = r.Y + r.H;
    return (r.X, bottom - glyphHeight, r.X + r.W, bottom);
}

/// <summary>
/// Locates each outline entry's title in its own page's text so annotations can be filed under
/// the heading that actually precedes them, rather than under whichever heading happens to come
/// first on the page. Headings whose title cannot be found (renumbered bookmarks, headings drawn
/// as images) are simply omitted — MarkdownBuilder falls back to page-level ordering for those.
/// </summary>
static Dictionary<string, float> ResolveHeadingPositions(
    IPdfService pdf,
    IPdfTextService textService,
    List<OutlineEntry> outline,
    Dictionary<int, string> pageTexts)
{
    var positions = new Dictionary<string, float>();

    var flattened = new List<(string key, string title, int page)>();
    void Flatten(List<OutlineEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Page is int page && pageTexts.ContainsKey(page))
                flattened.Add((MarkdownBuilder.HeadingKey(entry.Title, entry.Page), entry.Title, page));
            Flatten(entry.Children);
        }
    }
    Flatten(outline);

    // One batched geometry call per page, matching MarkupPlanService's approach.
    foreach (var group in flattened.GroupBy(h => h.page))
    {
        var pageText = pageTexts[group.Key];

        var resolved = new List<(string key, int start, int length)>();
        foreach (var (key, title, _) in group)
        {
            if (positions.ContainsKey(key)) continue;
            if (string.IsNullOrWhiteSpace(title)) continue;

            var range = TextLocator.ResolveQuote(pageText, title);
            if (range is not null)
                resolved.Add((key, range.Value.CharStart, range.Value.CharLength));
        }

        if (resolved.Count == 0) continue;

        try
        {
            var rectLines = textService.GetTextRangeRects(
                pdf.PdfBytes, group.Key, resolved.Select(r => (r.start, r.length)).ToList());

            for (int i = 0; i < resolved.Count && i < rectLines.Count; i++)
            {
                if (rectLines[i].Count == 0) continue;
                positions[resolved[i].key] = rectLines[i].Min(r => r.Top);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Warning: Could not locate headings on page {group.Key + 1}: {ex.Message}");
        }
    }

    return positions;
}

static AnnotationFile FilterPages(AnnotationFile src, Func<int, List<Annotation>, bool> pred)
{
    var dst = new AnnotationFile
    {
        Version = src.Version, SourcePdf = src.SourcePdf,
        SourcePdfPath = src.SourcePdfPath, Bookmarks = src.Bookmarks,
    };
    foreach (var (k, v) in src.Pages)
        if (pred(k, v)) dst.Pages[k] = v;
    return dst;
}

static AnnotationFile FilterAnnotations(AnnotationFile src, Func<Annotation, bool> pred)
{
    var dst = new AnnotationFile
    {
        Version = src.Version, SourcePdf = src.SourcePdf,
        SourcePdfPath = src.SourcePdfPath, Bookmarks = src.Bookmarks,
    };
    foreach (var (k, v) in src.Pages)
    {
        var filtered = v.Where(pred).ToList();
        if (filtered.Count > 0) dst.Pages[k] = filtered;
    }
    return dst;
}

static HashSet<string>? ParseColorFilter(string input)
{
    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var color = NormalizeColor(part);
        if (string.IsNullOrEmpty(color)) return null;
        result.Add(color);
    }
    return result;
}

static string NormalizeColor(string color)
{
    var c = color.TrimStart('#').ToLowerInvariant();
    if (c.Length == 3) c = $"{c[0]}{c[0]}{c[1]}{c[1]}{c[2]}{c[2]}";
    return c;
}

static List<int> ParsePageRange(string range, int maxPage)
{
    var pages = new List<int>();
    foreach (var part in range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (part.Contains('-'))
        {
            var bounds = part.Split('-', 2);
            var start = int.Parse(bounds[0]);
            var end = bounds.Length > 1 && !string.IsNullOrEmpty(bounds[1])
                ? int.Parse(bounds[1]) : maxPage;
            for (int p = start; p <= end; p++) pages.Add(p);
        }
        else
        {
            pages.Add(int.Parse(part));
        }
    }
    return pages;
}

static void PrintUsage()
{
    Console.WriteLine("""
        railmark — Convert PDFs to structured Markdown

        Usage: railmark <pdf> [options]

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
                               JSON markup plan into a new PDF (default: <pdf-stem>-marked.pdf,
                               or -o <path>). The input PDF is never modified unless --in-place
                               is given. Reports per-entry results as JSON on stdout.
          --in-place           With --apply-markup, write annotations directly into the input
                               PDF instead of a copy. Cannot be combined with -o.
          --dry-run            With --apply-markup, resolve and report only; do not write
          --version            Show the railmark version
          -h, --help           Show this help
        """);
}
