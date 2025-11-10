using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;
using RepoQL.Indexing.FileSystems;

namespace RepoQL.Indexing.Tests.FileSystems;

internal class CompositeFileSystemTests
{
    [Test]
    [DisplayName("Enumerate returns files from primary and additional mounts")]
    public async Task EnumerateAsync_Returns_All_Mounted_Files()
    {
        // Arrange
        var primary = new MemoryFileSystem("primary");
        primary.AddOrUpdateText("docs/readme.md", "primary");

        var github = new MemoryFileSystem("octocat/hello-world");
        github.AddOrUpdateText("src/index.md", "github");

        var composite = new CompositeFileSystem(
            CompositeFileSystemMount.CreatePrimary(primary),
            new[]
            {
                CompositeFileSystemMount.ForScheme(
                    id: "github:octocat/hello-world",
                    fileSystem: github,
                    scheme: github.Scheme,
                    authority: "octocat",
                    pathPrefix: "hello-world")
            });

        // Act
        var resources = await ReadAllAsync(composite);

        // Assert
        resources.Should().HaveCount(2);
        resources.Should().Contain(r => r.Uri.AbsoluteUri.StartsWith("mem://primary", StringComparison.OrdinalIgnoreCase));
        resources.Should().Contain(r => r.Uri.AbsoluteUri.StartsWith("mem://octocat/hello-world", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    [DisplayName("GetFile routes URIs to the correct mount")]
    public void GetFile_Resolves_To_Matching_Mount()
    {
        // Arrange
        var primary = new MemoryFileSystem("primary");
        primary.AddOrUpdateText("readme.md", "primary");

        var references = new MemoryFileSystem("reference");
        references.AddOrUpdateText("docs/guide.md", "reference");

        var composite = new CompositeFileSystem(
            CompositeFileSystemMount.CreatePrimary(primary),
            new[]
            {
                CompositeFileSystemMount.ForScheme(
                    id: "reference",
                    fileSystem: references,
                    scheme: references.Scheme,
                    authority: "reference")
            });

        var referenceUri = RepoUri.Parse("mem://reference/docs/guide.md");
        var primaryUri = RepoUri.Parse("mem://primary/readme.md");

        // Act
        var referenceFile = composite.GetFile(referenceUri);
        var primaryFile = composite.GetFile(primaryUri);

        // Assert
        ReadAll(referenceFile).Should().Be("reference");
        ReadAll(primaryFile).Should().Be("primary");
    }

    [Test]
    [DisplayName("Removing a mount unregisters it while leaving the primary intact")]
    public void RemoveMount_Unregisters_By_Id()
    {
        // Arrange
        var primary = new MemoryFileSystem("primary");
        var secondary = new MemoryFileSystem("secondary");
        var composite = new CompositeFileSystem(
            CompositeFileSystemMount.CreatePrimary(primary),
            new[]
            {
                CompositeFileSystemMount.ForScheme(
                    id: "secondary",
                    fileSystem: secondary,
                    scheme: secondary.Scheme,
                    authority: "secondary")
            });

        // Act
        composite.RemoveMount("secondary").Should().BeTrue();

        // Assert
        var missing = RepoUri.Parse("mem://secondary/file.txt");
        var missingFile = composite.GetFile(missing);
        missingFile.Exists.Should().BeFalse("removing the secondary mount should fall back to the primary 'mem' store");

        var primaryUri = RepoUri.Parse("mem://primary/any.txt");
        var info = composite.GetFile(primaryUri);
        info.Exists.Should().BeFalse();
    }

    private static async Task<List<EnumeratedResource>> ReadAllAsync(IMultiFileSystem fileSystem)
    {
        var results = new List<EnumeratedResource>();
        await foreach (var resource in fileSystem.EnumerateAsync(CancellationToken.None))
        {
            results.Add(resource);
        }

        return results;
    }

    private static string ReadAll(IFileInfo file)
    {
        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
