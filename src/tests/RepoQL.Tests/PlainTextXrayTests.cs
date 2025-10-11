using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.FileSystem.InMemory;

namespace RepoQL.Tests;

internal class PlainTextXrayTests
{
    [Test]
    public async Task PlainTextLoader_Populates_XRay_Headline_And_Terse_Summary()
    {
        // Arrange a simple in-memory text file (3 lines, no trailing newline)
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

        // Ensure media kind is set for better labels
        await loader.CanLoadAsync(discovered);

        // Act
        var document = await loader.LoadAsync(discovered);
        var records = loader.Materialize(document);
        var artifact = records.Artifacts.Single();

        // Assert headline: file name, kind or base, size, and line count present
        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Headline!.Should().Contain("a.txt");
        artifact.Headline.Should().Contain("plain.document");
        artifact.Headline.Should().Contain("lines");

        // Assert summary: terse two-line form and includes type + lines
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        var lines = artifact.Summary!.Split('\n');
        lines.Length.Should().BeGreaterThanOrEqualTo(2);
        lines[0].Should().StartWith("Type: plain.document");
        lines[1].ToLowerInvariant().Should().Contain("lines");

        // Structure is not populated for plain fallback
        artifact.Structure.Should().BeNull();
    }
}

