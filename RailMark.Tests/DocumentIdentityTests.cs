using System.Security.Cryptography;
using RailReader.Core.Services;

namespace RailMark.Tests;

public sealed class DocumentIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docid").FullName;

    public DocumentIdentityTests()
    {
        DocumentIdentity.OverrideMapPathForTesting(Path.Combine(_dir, "document_ids.json"));
    }

    public void Dispose()
    {
        DocumentIdentity.OverrideMapPathForTesting(null);
        Directory.Delete(_dir, true);
    }

    private string WritePdf(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    // The map exactly as RailReader2 writes it (snake_case, with fields RailMark doesn't read).
    private void WriteMap(string files, string aliases = "{}") => File.WriteAllText(DocumentIdentity.MapPath,
        $$"""{ "version": 1, "files": {{files}}, "aliases": {{aliases}}, "paths": {} }""");

    [Fact]
    public void Lookup_Returns_The_Id_Recorded_For_The_Path()
    {
        var pdf = WritePdf("a.pdf", "first");
        WriteMap($$"""{ "{{pdf}}": { "size": 5, "mtime_ticks": 1, "id": "doc-a" } }""");

        // Even after the bytes changed (annotations written back): the path keeps its id.
        File.WriteAllText(pdf, "rewritten");
        Assert.Equal("doc-a", DocumentIdentity.Lookup(pdf));
    }

    [Fact]
    public void Lookup_Recognises_A_Copy_By_Its_Bytes()
    {
        var original = WritePdf("a.pdf", "same bytes");
        var copy = WritePdf("moved.pdf", "same bytes");
        WriteMap($$"""{ "{{original}}": { "size": 10, "mtime_ticks": 1, "id": "doc-a" } }""",
                 $$"""{ "{{Hash(original)}}": "doc-a" }""");

        Assert.Equal("doc-a", DocumentIdentity.Lookup(copy));
    }

    [Fact]
    public void Lookup_Keeps_A_Copys_Id_After_Its_Bytes_Change_In_The_Same_Run()
    {
        var original = WritePdf("a.pdf", "same bytes");
        var copy = WritePdf("moved.pdf", "same bytes");
        WriteMap("{}", $$"""{ "{{Hash(original)}}": "doc-a" }""");

        Assert.Equal("doc-a", DocumentIdentity.Lookup(copy));
        // A native save rewrites the copy between Core's Load and Save: it must keep its sidecar.
        File.WriteAllText(copy, "annotated bytes");
        Assert.Equal("doc-a", DocumentIdentity.Lookup(copy));
    }

    [Fact]
    public void Lookup_Is_Null_For_A_Document_RailReader2_Never_Opened()
    {
        WriteMap("{}");
        Assert.Null(DocumentIdentity.Lookup(WritePdf("new.pdf", "unseen")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    public void Lookup_Is_Null_When_The_Map_Is_Missing_Or_Unreadable(string? map)
    {
        if (map != null) File.WriteAllText(DocumentIdentity.MapPath, map);
        Assert.Null(DocumentIdentity.Lookup(WritePdf("a.pdf", "bytes")));
    }

    [Fact]
    public void Lookup_Never_Writes_The_Map()
    {
        WriteMap("{}");
        var before = File.ReadAllText(DocumentIdentity.MapPath);

        DocumentIdentity.Lookup(WritePdf("a.pdf", "bytes"));

        Assert.Equal(before, File.ReadAllText(DocumentIdentity.MapPath));
    }

    [Fact]
    public void Core_Names_The_Sidecar_By_The_Looked_Up_Id()
    {
        var pdf = WritePdf("a.pdf", "first");
        WriteMap($$"""{ "{{pdf}}": { "size": 5, "mtime_ticks": 1, "id": "doc-a" } }""");
        var store = new AnnotationService { DocumentKeyResolver = DocumentIdentity.Lookup };

        Assert.Equal("doc-a.json", Path.GetFileName(store.GetStoragePath(pdf)));
    }
}
