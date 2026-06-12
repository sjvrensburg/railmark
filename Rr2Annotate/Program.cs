using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Export;
using RailReader.Renderer.Skia;
using Rr2Annotate.Services;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    PrintUsage();
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

if (!stdoutMode)
    outputPath ??= exportMode
        ? Path.ChangeExtension(pdfPath, null) + "-export.md"
        : Path.ChangeExtension(pdfPath, null) + "-annotations.md";

// Initialise PDFium native library
PdfiumResolver.Initialize();
var factory = new SkiaPdfServiceFactory();

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

    if (stdoutMode)
    {
        await exporter.ExportAsync(pdfPath, Console.Out, exportOptions,
            new Progress<ExportProgress>(p => Console.Error.WriteLine($"  {p.Status}")));
    }
    else
    {
        using var sw = new StreamWriter(outputPath!, append: false, System.Text.Encoding.UTF8);
        await exporter.ExportAsync(pdfPath, sw, exportOptions,
            new Progress<ExportProgress>(p => Console.Error.WriteLine($"  {p.Status}")));
        Console.Error.WriteLine($"Written to: {outputPath}");
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

// Pre-extract per-page text and highlight text from PDF character boxes
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
        if (pageText != null && annotations[i] is HighlightAnnotation highlight && highlight.Rects.Count > 0)
        {
            var parts = highlight.Rects
                .Select(r => pageText.ExtractTextInRect(r.X, r.Y, r.X + r.W, r.Y + r.H))
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            if (parts.Count > 0)
                highlightTexts[(pageIdx, i)] = string.Join(" ", parts);
        }
    }
}

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
    imageRelDir);

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
        rr2annotate — Export RailReader2 annotations as Markdown

        Usage: rr2annotate <pdf> [options]

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
        """);
}
