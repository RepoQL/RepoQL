using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.FileSystem.Physical;

namespace RepoQL.FileSystem.Tests;

public class PhysicalFileSystemPathTests
{
    [Test]
    public void ToAbsolutePath_CombinesRootAndRelativeSegments()
    {
        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var uri = RepoUri.Parse("file:///dir/sub/File.txt");

        var resolved = FileUriPathResolver.Resolve(root, uri);

        resolved.RelativePath.Should().Be("dir/sub/File.txt");
        AssertPathEquals(resolved.AbsolutePath, Path.Combine(root, "dir", "sub", "File.txt"));
    }

    [Test]
    public void ToAbsolutePath_DecodesPercentEncoding()
    {
        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var uri = RepoUri.Parse("file:///docs/My%20File%20%231.md");

        var resolved = FileUriPathResolver.Resolve(root, uri);

        resolved.RelativePath.Should().Be("docs/My File #1.md");
        AssertPathEquals(resolved.AbsolutePath, Path.Combine(root, "docs", "My File #1.md"));
    }

    [Test]
    public void ToAbsolutePath_AllowsTildeCharacters()
    {
        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var uri = RepoUri.Parse("file:///docs/Plan~draft.txt");

        var resolved = FileUriPathResolver.Resolve(root, uri);

        resolved.RelativePath.Should().Be("docs/Plan~draft.txt");
        AssertPathEquals(resolved.AbsolutePath, Path.Combine(root, "docs", "Plan~draft.txt"));
    }

    [Test]
    public void ToAbsolutePath_ReturnsRootForContainerUri()
    {
        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var uri = RepoUri.Parse("file:///");

        var resolved = FileUriPathResolver.Resolve(root, uri);

        resolved.RelativePath.Should().Be(string.Empty);
        AssertPathEquals(resolved.AbsolutePath, root);
    }

    [Test]
    public void ToAbsolutePath_NormalizesParentSegmentsWithinRoot()
    {
        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var uri = RepoUri.Parse("file:///%2E%2E/outside.txt");

        var resolved = FileUriPathResolver.Resolve(root, uri);

        resolved.RelativePath.Should().Be("outside.txt");
        AssertPathEquals(resolved.AbsolutePath, Path.Combine(root, "outside.txt"));
    }

    [Test]
    public void ToAbsolutePath_ThrowsForNonFileScheme()
    {
        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var uri = RepoUri.Parse("mem://repo/docs/file.txt");

        Action act = () => FileUriPathResolver.Resolve(root, uri);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void GetFile_ResolvesFileWithSpacesAndTilde()
    {
        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var target = Path.Combine(root, "docs", "My File ~.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "hello");

        var store = new PhysicalFileSystem(root);
        var uri = RepoUri.Parse("file:///docs/My%20File%20~.txt");

        var file = store.GetFile(uri);

        file.Exists.Should().BeTrue();
        file.PhysicalPath.Should().NotBeNull();
        AssertPathEquals(file.PhysicalPath!, target);

        // FileProviders should return a relative path lookup without mangling
        var resolved = FileUriPathResolver.Resolve(root, uri);
        resolved.RelativePath.Should().Be("docs/My File ~.txt");
    }

    [Test]
    public void ToRepoUri_CaseVariantAbsolutePaths_ProduceSameUriOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TempRoot();
        var root = temp.DirectoryPath;
        var docsPath = Path.Combine(root, "Docs");
        Directory.CreateDirectory(docsPath);
        var actualPath = Path.Combine(docsPath, "ReadMe.md");
        File.WriteAllText(actualPath, "hello");

        var caseVariantPath = Path.Combine(root.ToUpperInvariant(), "DOCS", "README.MD");
        var store = new PhysicalFileSystem(root);

        var actualUri = store.ToRepoUri(actualPath);
        var variantUri = store.ToRepoUri(caseVariantPath);

        actualUri.AbsoluteUri.Should().Be(variantUri.AbsoluteUri);
        actualUri.AbsoluteUri.Should().Be(actualUri.AbsoluteUri.ToLowerInvariant());
    }

    private static void AssertPathEquals(string actual, string expected)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var actualFull = Path.GetFullPath(actual);
        var expectedFull = Path.GetFullPath(expected);
        comparer.Equals(actualFull, expectedFull).Should().BeTrue($"Actual: {actual}; Expected: {expected}");
    }

    private sealed class TempRoot : IDisposable
    {
        public string DirectoryPath { get; }

        public TempRoot()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "RepoQL",
                nameof(PhysicalFileSystemPathTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup for tests
            }
        }
    }
}
