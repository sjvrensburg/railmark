using System.Diagnostics;

namespace RailMark.Services.Docx;

/// <summary>
/// Renders a .docx to PDF with headless LibreOffice, exporting Word bookmarks as named
/// destinations — the anchors <see cref="DocxReviewPreparer"/> planted.
/// </summary>
/// <remarks>
/// Each run uses its own throw-away LibreOffice profile: a headless <c>soffice</c> that finds the
/// user's desktop instance (or another conversion) holding the default profile exits at once
/// without converting anything, and says nothing about why.
/// </remarks>
internal static class LibreOfficeConverter
{
    /// <summary>writer_pdf_Export options. Comments are not exported: they were removed, and are
    /// written as real annotations afterwards.</summary>
    private const string FilterOptions =
        "{\"ExportBookmarksToPDFDestination\":{\"type\":\"boolean\",\"value\":\"true\"},"
        + "\"ExportNotes\":{\"type\":\"boolean\",\"value\":\"false\"},"
        + "\"ExportNotesInMargin\":{\"type\":\"boolean\",\"value\":\"false\"}}";

    public static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return File.Exists(explicitPath) ? explicitPath : null;
        if (Environment.GetEnvironmentVariable("RAILMARK_SOFFICE") is { Length: > 0 } env && File.Exists(env))
            return env;

        var names = OperatingSystem.IsWindows() ? new[] { "soffice.exe", "soffice.com" } : new[] { "soffice", "libreoffice" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in names)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }

        string[] wellKnown = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
            ]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/LibreOffice.app/Contents/MacOS/soffice"]
                : ["/usr/bin/soffice", "/usr/lib/libreoffice/program/soffice", "/opt/libreoffice/program/soffice", "/snap/bin/libreoffice"];
        return wellKnown.FirstOrDefault(File.Exists);
    }

    /// <summary>Converts <paramref name="docxPath"/> to <paramref name="pdfPath"/>; throws with LibreOffice's output on failure.</summary>
    public static void Convert(string soffice, string docxPath, string pdfPath, TimeSpan timeout)
    {
        var work = Directory.CreateTempSubdirectory("railmark-lo-");
        try
        {
            var profile = new Uri(Path.Combine(work.FullName, "profile") + Path.DirectorySeparatorChar).AbsoluteUri;
            var outDir = Path.Combine(work.FullName, "out");
            Directory.CreateDirectory(outDir);

            var psi = new ProcessStartInfo(soffice)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[]
                     {
                         $"-env:UserInstallation={profile}", "--headless", "--norestore", "--nologo",
                         "--convert-to", "pdf:writer_pdf_Export:" + FilterOptions, "--outdir", outDir,
                         Path.GetFullPath(docxPath),
                     })
                psi.ArgumentList.Add(a);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {soffice}.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                throw new TimeoutException($"LibreOffice did not finish within {timeout.TotalSeconds:0} s.");
            }

            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(docxPath) + ".pdf");
            if (process.ExitCode != 0 || !File.Exists(produced))
                throw new InvalidOperationException(
                    $"LibreOffice produced no PDF (exit code {process.ExitCode}).\n{stdout.Result}{stderr.Result}".TrimEnd());

            File.Copy(produced, pdfPath, overwrite: true);
        }
        finally
        {
            try { work.Delete(recursive: true); } catch { /* best effort */ }
        }
    }
}
