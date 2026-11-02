using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailMark.Services.Docx;

/// <summary>The <c>--from-docx</c> pipeline: prepare → render → anchor → annotate → save.</summary>
internal static class DocxImport
{
    private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(5);

    public static int Run(string docxPath, string outputPdf, string? sofficeArg, string? keepBaseDocx, IPdfServiceFactory factory)
    {
        var soffice = LibreOfficeConverter.Locate(sofficeArg);
        if (soffice is null)
        {
            Console.Error.WriteLine(sofficeArg is null
                ? "Error: LibreOffice (soffice) not found. Install it, or pass --soffice <path> or set RAILMARK_SOFFICE."
                : $"Error: --soffice: not found: {sofficeArg}");
            return 1;
        }

        var work = Directory.CreateTempSubdirectory("railmark-docx-");
        try
        {
            // The prepared copy keeps the input's file name: LibreOffice names its output after it,
            // and a PDF's /Title defaults to it.
            var baseDocx = Path.Combine(work.FullName, Path.GetFileName(docxPath));
            DocxReview review;
            try
            {
                review = DocxReviewPreparer.Prepare(docxPath, baseDocx);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException)
            {
                Console.Error.WriteLine($"Error: {docxPath} is not a readable .docx: {ex.Message}");
                return 1;
            }
            if (keepBaseDocx is not null) File.Copy(baseDocx, keepBaseDocx, overwrite: true);

            Console.Error.WriteLine($"Rendering the original text with {soffice}…");
            try
            {
                LibreOfficeConverter.Convert(soffice, baseDocx, outputPdf, ConvertTimeout);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                Console.Error.WriteLine($"Error: conversion failed: {ex.Message}");
                return 1;
            }

            var pdf = factory.CreatePdfService(outputPdf);
            var (file, results) = DocxAnnotationBuilder.Build(
                review, pdf, factory.CreatePdfTextService(), factory.CreatePdfLinkService());

            // The anchors have been read: take them out of the output.
            var rendered = File.ReadAllBytes(outputPdf);
            if (NamedDestinationScrubber.Scrub(rendered, review.MarkPrefix) > 0)
                File.WriteAllBytes(outputPdf, rendered);

            if (file.Pages.Values.Any(l => l.Count > 0))
            {
                CompositeAnnotationStore.Default.OnSidecarFallback = (path, reason) =>
                    Console.Error.WriteLine($"Warning: {path} — falling back to sidecar storage ({reason}).");
                bool saved;
                try
                {
                    saved = CompositeAnnotationStore.Default.Save(outputPdf, file);
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
            }
            Console.Error.WriteLine($"Written to: {outputPdf}");

            Console.Write(JsonSerializer.Serialize(results, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }));
            Console.WriteLine();

            int placed = results.Count(r => r.Success);
            var byKind = string.Join(", ", results.Where(r => r.Success).GroupBy(r => r.Kind)
                .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}"));
            Console.Error.WriteLine($"Placed {placed}/{results.Count} review items" + (byKind.Length > 0 ? $" ({byKind})." : "."));
            foreach (var r in results.Where(r => !r.Success))
                Console.Error.WriteLine($"  FAILED {r.Kind} {r.Id}{By(r)} — {r.Error}");
            foreach (var r in results.Where(r => r.Warning != null))
                Console.Error.WriteLine($"  WARNING page {r.Page}: {r.Kind} {r.Id}{By(r)} — {r.Warning}");
            foreach (var (what, n) in review.Unsupported)
                Console.Error.WriteLine($"  NOTE: {n} {what} revision(s) not annotated; the current value is rendered.");

            return results.All(r => r.Success) ? 0 : 2;
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { /* best effort */ }
        }
    }

    private static string By(DocxItemResult r) => r.Author is null ? "" : $" by {r.Author}";
}
