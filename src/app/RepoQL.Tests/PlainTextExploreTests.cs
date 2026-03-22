using System.Text;
using RepoQL.Data.DuckDB;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.FileSystem.InMemory;

namespace RepoQL.Tests;

internal class PlainTextExploreTests
{
    [Test]
    public async Task PlainTextLoader_Populates_Explore_Headline_And_Terse_Summary()
    {
        var fs = new MemoryFileSystem("repo");
        var content = "one\nsecond\nthird";
        fs.AddOrUpdateText("a.txt", content);
        var uri = RepoUri.Parse("mem://repo/a.txt");

        var loader = new PlainTextLoader();
        var discovered = new DiscoveredArtifact
        {
            File = fs.GetFile(uri),
            RepoUri = uri
        };

        await loader.CanLoadAsync(discovered);

        var document = await loader.LoadAsync(discovered);
        var records = loader.Materialize(document);
        var artifact = records.Artifacts.Single();

        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Headline!.Should().Contain("a.txt");
        artifact.Headline.Should().Contain("plain.document");
        artifact.Headline.Should().Contain("lines");

        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        var lines = artifact.Summary!.Split('\n');
        lines.Length.Should().BeGreaterThanOrEqualTo(2);
        lines[0].Should().StartWith("Type: plain.document");
        lines[1].ToLowerInvariant().Should().Contain("lines");

        artifact.Structure.Should().BeNull();
    }

    [Test]
    public async Task PlainTextLoader_BinaryMedia_IsIndexedWithoutTextDecoding()
    {
        var bytes = Encoding.ASCII.GetBytes("II*\\0binary-tiff-payload");
        var file = new FakeFileInfo("scan.tiff", bytes);
        var uri = RepoUri.Parse("file:///scan.tiff");

        var loader = new PlainTextLoader();
        var discovered = new DiscoveredArtifact
        {
            File = file,
            RepoUri = uri,
            MediaType = SemanticMediaType.Create("image", "tiff")
        };

        var document = await loader.LoadAsync(discovered);
        var records = loader.Materialize(document);
        var artifact = records.Artifacts.Single();

        document.Text.Should().BeEmpty();
        artifact.Text.Should().BeEmpty();
        artifact.Size.Should().Be(bytes.Length);
        artifact.MediaType!.Type.Should().Be("image");
        artifact.MediaType.Subtype.Should().Be("tiff");
        artifact.Headline.Should().Contain("scan.tiff");
        artifact.Headline.Should().Contain("image/tiff");
        artifact.Headline.Should().Contain("content omitted");
        artifact.Summary.Should().Contain("binary media is indexed as metadata only");
        artifact.Digest.Should().StartWith("xxh64:");
    }

    [Test]
    public async Task PlainTextLoader_OversizedText_IsIndexedWithoutLoadingContent()
    {
        var bytes = Encoding.UTF8.GetBytes("tiny body");
        var file = new FakeFileInfo("huge.log", bytes, reportedLength: 9);
        var uri = RepoUri.Parse("file:///huge.log");

        var loader = new PlainTextLoader(maxTextReadBytes: 8);
        var discovered = new DiscoveredArtifact
        {
            File = file,
            RepoUri = uri,
            MediaType = SemanticMediaType.Create("text", "plain").WithKind("plain.document")
        };

        var document = await loader.LoadAsync(discovered);
        var records = loader.Materialize(document);
        var artifact = records.Artifacts.Single();

        document.Text.Should().BeEmpty();
        artifact.Text.Should().BeEmpty();
        artifact.Size.Should().Be(9);
        artifact.Headline.Should().Contain("huge.log");
        artifact.Headline.Should().Contain("content omitted");
        artifact.Summary.Should().Contain("8 byte safety limit");
        artifact.Digest.Should().StartWith("xxh64:");
    }

    private sealed class FakeFileInfo(string name, byte[] bytes, long? reportedLength = null) : IFileInfo
    {
        public bool Exists => true;
        public long Length => reportedLength ?? bytes.Length;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.Now;
        public bool IsDirectory => false;

        public Stream CreateReadStream() => new MemoryStream(bytes, writable: false);
    }
}
