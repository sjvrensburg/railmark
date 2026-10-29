using System.Security.Cryptography;
using System.Text.Json;
using RailReader.Core.Services;

namespace RailMark.Services;

/// <summary>
/// The key RailReader2 names a document's annotation sidecar by, so that RailMark reads and writes
/// the same <c>annotations/&lt;key&gt;.json</c> for a signed or read-only PDF. RailReader2 keys it
/// by a content-based id it keeps in <c>ConfigDir/document_ids.json</c>; this is a read-only copy of
/// its <c>DocumentIdentity.Lookup</c>, which lives in <c>RailReader2.Shared</c> and not in any
/// package (RailReaderCore#159 asks to move it into Core — drop this copy when it does).
///
/// Read-only: the map is never written, since RailReader2 may hold it in memory. A document
/// RailReader2 has never opened has no key, and Core then falls back to naming by path hash.
/// </summary>
internal static class DocumentIdentity
{
    internal static string MapPath => MapPathOverride ?? Path.Combine(AppConfig.ConfigDir, "document_ids.json");

    internal static string? MapPathOverride { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static IdentityMap? s_map;

    /// <summary>
    /// The id RailReader2 last resolved for this path, else the id it recorded for these exact
    /// bytes (a moved, renamed or copied file), else null. Hashes the file only when the path is
    /// unknown.
    /// </summary>
    public static string? Lookup(string pdfPath)
    {
        string full = Path.GetFullPath(pdfPath);
        var map = Map();
        if (map.Files.TryGetValue(full, out var known)) return known.Id;
        if (!File.Exists(full)) return null;
        return map.Aliases.GetValueOrDefault(HashFile(full));
    }

    private static IdentityMap Map() => s_map ??= Load();

    // Absent, empty or unreadable all mean "no ids known", as in RailReader2.
    private static IdentityMap Load()
    {
        try
        {
            if (!File.Exists(MapPath)) return new IdentityMap();
            return JsonSerializer.Deserialize<IdentityMap>(File.ReadAllText(MapPath), JsonOptions) ?? new IdentityMap();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new IdentityMap();
        }
    }

    internal static void ResetForTests() => s_map = null;

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed class FileEntry
    {
        public string Id { get; set; } = "";
    }

    private sealed class IdentityMap
    {
        public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.Ordinal);
    }
}
