using System.IO;
using AwesomeAssertions;
using RepoQL.Contracts;

namespace RepoQL.Protocol.Tests;

public class RepoLocatorTests
{
    [Test]
    public void TryFindRepoRoot_FindsNearestMarker()
    {
        using var temp = new TempDir();
        var markerRoot = Path.Combine(temp.Path, "repo");
        var nested = Path.Combine(markerRoot, "nested", "child");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(markerRoot, ".git"), string.Empty);

        var found = RepoLocator.TryFindRepoRoot(nested, out var root, out var searchedFrom, allowFallback: false);

        found.Should().BeTrue();
        root.Should().Be(Path.GetFullPath(markerRoot));
        searchedFrom.Should().Be(Path.GetFullPath(nested));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath()) ?? System.IO.Path.GetPathRoot(Environment.CurrentDirectory) ?? ".";
            Path = System.IO.Path.Combine(root, $"repoql-protocol-tests-{Guid.NewGuid()}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
