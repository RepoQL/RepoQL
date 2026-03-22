using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Documentation;
using RepoQL.FileSystem.Embedded;

namespace RepoQL.Tests;

public class EmbeddedStoreManifestPathMappingTests
{
    [Test]
    public async Task EnumerateAsync_UsesManifestDirectoryNames_ForCanonicalUris()
    {
        var asm = typeof(DocumentationMarker).Assembly;
        var store = new EmbeddedStore(asm, DocumentationFileSystem.Scheme);

        var uris = new List<string>();
        await foreach (var file in store.EnumerateAsync(CancellationToken.None))
            uris.Add(store.GetUri(file).AbsoluteUri);

        uris.Should().Contain("help:///skills/mermaid-diagrams/SKILL.md");
        uris.Should().NotContain("help:///skills/mermaid_diagrams/SKILL.md");
    }

    [Test]
    public async Task GetFile_ResolvesHyphenatedCanonicalUri()
    {
        var asm = typeof(DocumentationMarker).Assembly;
        var store = new EmbeddedStore(asm, DocumentationFileSystem.Scheme);

        var uri = RepoUri.Parse("help:///skills/mermaid-diagrams/SKILL.md");
        var file = store.GetFile(uri);

        file.Exists.Should().BeTrue();
        file.PhysicalPath.Should().Be("/skills/mermaid-diagrams/SKILL.md");

        using var reader = new StreamReader(file.CreateReadStream());
        var content = await reader.ReadToEndAsync();

        content.Should().Contain("# Mermaid Diagrams");
    }
}
