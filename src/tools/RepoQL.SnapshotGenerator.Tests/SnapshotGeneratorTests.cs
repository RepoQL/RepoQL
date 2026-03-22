using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Snapshots;
using RepoQL.Data.DuckDB;
using RepoQL.Data.DuckDB.Snapshots;
using RepoQL.SnapshotGenerator;

namespace RepoQL.SnapshotGenerator.Tests;

public class SnapshotGeneratorTests
{
    [Test]
    [DisplayName("Generator produces valid manifest from single markdown file")]
    public async Task GenerateAsync_SingleFile_ProducesValidManifest()
    {
        using var tempDir = new TempDocDirectory();
        tempDir.WriteFile("test.md", "# Hello\n\nWorld");

        var manifest = await SnapshotGeneratorCore.GenerateAsync(tempDir.Path, "1.0.0");

        manifest.FormatVersion.Should().Be("1");
        manifest.SourceId.Should().Be("help-docs");
        manifest.Version.Should().Be("1.0.0");
        manifest.Documents.Should().HaveCount(1);
        manifest.Documents[0].Uri.Should().Be("help:///test.md");
    }

    [Test]
    [DisplayName("Generator produces correct URIs for nested directories")]
    public async Task GenerateAsync_NestedDirs_ProducesCorrectUris()
    {
        using var tempDir = new TempDocDirectory();
        tempDir.WriteFile("repoql/commands/help.md", "# Help\n\nUsage info");
        tempDir.WriteFile("repoql/tools/query/schema.md", "# Schema\n\nTable info");

        var manifest = await SnapshotGeneratorCore.GenerateAsync(tempDir.Path, "1.0.0");

        manifest.Documents.Should().HaveCount(2);
        var uris = manifest.Documents.Select(d => d.Uri).OrderBy(u => u).ToList();
        uris[0].Should().Be("help:///repoql/commands/help.md");
        uris[1].Should().Be("help:///repoql/tools/query/schema.md");
    }

    [Test]
    [DisplayName("Generator skips non-markdown files")]
    public async Task GenerateAsync_MixedFiles_SkipsNonMd()
    {
        using var tempDir = new TempDocDirectory();
        tempDir.WriteFile("doc.md", "# Doc");
        tempDir.WriteFile("data.csv", "a,b,c\n1,2,3");
        tempDir.WriteFile("notes.txt", "plain text");

        var manifest = await SnapshotGeneratorCore.GenerateAsync(tempDir.Path, "1.0.0");

        manifest.Documents.Should().HaveCount(1);
        manifest.Documents[0].Uri.Should().Be("help:///doc.md");
    }

    [Test]
    [DisplayName("Generated artifacts have non-null x-ray summaries")]
    public async Task GenerateAsync_ProducesXRaySummaries()
    {
        using var tempDir = new TempDocDirectory();
        tempDir.WriteFile("doc.md", """
            ---
            description: Test document with sections
            ---

            # Main Title

            ## Section One

            Content for section one.

            ## Section Two

            Content for section two.
            """);

        var manifest = await SnapshotGeneratorCore.GenerateAsync(tempDir.Path, "1.0.0");

        var artifact = manifest.Documents[0].Artifact;
        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().Contain("Section One");
        artifact.Structure.Should().Contain("Section Two");
    }

    [Test]
    [DisplayName("Generated digests match ContentDigest.FromBytes")]
    public async Task GenerateAsync_DigestsMatchContentDigest()
    {
        using var tempDir = new TempDocDirectory();
        var content = "# Test\n\nSome content for digest verification.";
        tempDir.WriteFile("test.md", content);

        var manifest = await SnapshotGeneratorCore.GenerateAsync(tempDir.Path, "1.0.0");

        var snapshotDigest = manifest.Documents[0].Artifact.Digest;
        var expectedDigest = ContentDigest.FromBytes(System.Text.Encoding.UTF8.GetBytes(content));

        snapshotDigest.Should().Be(expectedDigest);
    }

    [Test]
    [DisplayName("Generated documents include section nodes and edges")]
    public async Task GenerateAsync_ProducesNodesAndEdges()
    {
        using var tempDir = new TempDocDirectory();
        tempDir.WriteFile("doc.md", "# Title\n\n## Section A\n\nText\n\n## Section B\n\nMore text");

        var manifest = await SnapshotGeneratorCore.GenerateAsync(tempDir.Path, "1.0.0");

        var doc = manifest.Documents[0];
        // Document node + heading nodes
        doc.Nodes.Count.Should().BeGreaterThan(1);
        // HAS_PART edges from document to headings
        doc.Edges.Count.Should().BeGreaterThan(0);
    }

    [Test]
    [DisplayName("Snapshot round-trip: generate → serialize → deserialize → load into DuckDB")]
    public async Task GenerateAsync_RoundTrip_LoadsIntoDuckDb()
    {
        using var tempDir = new TempDocDirectory();
        tempDir.WriteFile("alpha.md", "# Alpha\n\nFirst doc");
        tempDir.WriteFile("beta.md", "# Beta\n\nSecond doc");

        var manifest = await SnapshotGeneratorCore.GenerateAsync(tempDir.Path, "1.0.0");

        // Serialize → deserialize round-trip
        var json = SnapshotSerializer.Serialize(manifest);
        var deserialized = SnapshotSerializer.Deserialize(json);

        // Load into DuckDB via SnapshotLoader
        using var db = new DuckDbDataStore(serviceProvider: new ServiceCollection().BuildServiceProvider());
        var source = new ManifestSnapshotSource("test", "1.0.0", "help://", deserialized);
        SnapshotLoader.LoadSource(db, source);

        // Query the loaded data
        var docs = db.Read(
            "SELECT uri FROM node WHERE kind = 'document' AND uri LIKE 'help://%' ORDER BY uri",
            r => r.GetString(0));
        docs.Should().HaveCount(2);
        docs[0].Should().Be("help:///alpha.md");
        docs[1].Should().Be("help:///beta.md");

        // Verify x-ray summaries are present
        var headlines = db.Read(
            "SELECT headline FROM artifact WHERE headline IS NOT NULL",
            r => r.GetString(0));
        headlines.Should().HaveCount(2);
    }

    [Test]
    [DisplayName("Generator processes real documentation directory")]
    public async Task GenerateAsync_RealDocs_ProducesExpectedCount()
    {
        var docsDir = FindDocumentationDirectory();
        if (docsDir is null)
        {
            Assert.Fail("Could not locate src/RepoQL.Documentation directory");
            return;
        }

        var manifest = await SnapshotGeneratorCore.GenerateAsync(docsDir, "test");

        manifest.Documents.Count.Should().BeGreaterThanOrEqualTo(30,
            "Expected at least 30 help:// documents from the documentation directory");

        // Every document should have a headline
        foreach (var doc in manifest.Documents)
        {
            doc.Artifact.Headline.Should().NotBeNullOrWhiteSpace(
                $"Document {doc.Uri} should have a headline");
        }
    }

    private static string? FindDocumentationDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "RepoQL.Documentation");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Adapter that wraps a deserialized <see cref="SnapshotManifest"/> as an <see cref="ISnapshotSource"/>.
    /// </summary>
    private sealed class ManifestSnapshotSource(
        string id, string version, string uriPrefix, SnapshotManifest manifest) : ISnapshotSource
    {
        public string Id => id;
        public string Version => version;
        public string UriPrefix => uriPrefix;

        public IReadOnlyList<SnapshotDocument> GetDocuments()
            => manifest.Documents.Select(SnapshotSerializer.FromDto).ToList();
    }

    /// <summary>
    /// Creates a temporary directory with markdown files for testing.
    /// </summary>
    private sealed class TempDocDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"repoql-snapshot-test-{Guid.NewGuid():N}");

        public TempDocDirectory() => Directory.CreateDirectory(Path);

        public void WriteFile(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', '\\'));
            var dir = System.IO.Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
