using Microsoft.Extensions.FileProviders;
namespace RepoQL.Protocol.Tests;

public class RepoqlSocketPathResolverTests
{
    [Test]
    public void ResolvePhysical_UsesOverridePath()
    {
        using var temp = new TempRepo();
        var overridePath = "sockets\\custom.sock";

        var resolved = RepoqlSocketPathResolver.ResolvePhysical(temp.Path, overridePath);

        var expected = Path.GetFullPath(Path.Combine(temp.Path, "sockets", "custom.sock"))
            .Replace('\\', '/');
        resolved.Should().Be(expected);
    }

    [Test]
    public void ResolvePhysical_UsesMappingFile()
    {
        using var temp = new TempRepo();
        var repoqlDir = RepoqlPaths.GetRepoqlDirectoryPath(temp.Path);
        Directory.CreateDirectory(repoqlDir);
        var mappedPath = "C:\\temp\\repoql\\mapped.sock";
        File.WriteAllText(Path.Combine(repoqlDir, RepoqlPaths.SocketMapFileName), mappedPath);

        var resolved = RepoqlSocketPathResolver.ResolvePhysical(temp.Path);

        resolved.Should().Be(mappedPath.Replace('\\', '/'));
    }

    [Test]
    public void TryReadRepoqlSocketMapping_ReturnsNullWhenMissing()
    {
        using var temp = new TempRepo();
        using var provider = new PhysicalFileProvider(temp.Path);

        var mapped = provider.TryReadRepoqlSocketMapping();

        mapped.Should().BeNull();
    }

    [Test]
    public void TryReadRepoqlSocketMapping_ReturnsNullWhenEmpty()
    {
        using var temp = new TempRepo();
        var repoqlDir = RepoqlPaths.GetRepoqlDirectoryPath(temp.Path);
        Directory.CreateDirectory(repoqlDir);
        File.WriteAllText(Path.Combine(repoqlDir, RepoqlPaths.SocketMapFileName), "   ");
        using var provider = new PhysicalFileProvider(temp.Path);

        var mapped = provider.TryReadRepoqlSocketMapping();

        mapped.Should().BeNull();
    }

    [Test]
    public void Resolve_UsesRedirectWhenDefaultPathTooLong()
    {
        var repoRoot = BuildLongRepoRoot();
        var provider = new NullFileProvider();
        var writer = new TestRepoqlFileWriter(repoRoot);

        var resolved = RepoqlSocketPathResolver.Resolve(repoRoot, provider, writer: writer);

        var fullRoot = Path.GetFullPath(repoRoot);
        var defaultSocket = RepoqlSocketPathResolver.NormalizeSocketPath(
            RepoqlPaths.GetDefaultSocketPath(fullRoot),
            fullRoot);
        var limit = GetPlatformSocketPathLimit();

        defaultSocket.Length.Should().BeGreaterThanOrEqualTo(limit);
        resolved.Should().NotBe(defaultSocket);
        resolved.Length.Should().BeLessThan(limit);
        writer.LastRelativePath.Should().Be(RepoqlPaths.SocketMapFileName);
        writer.LastContents.Should().NotBeNull();
        writer.LastContents!.Trim().Replace('\\', '/').Should().Be(resolved);
    }

    private sealed class TempRepo : IDisposable
    {
        public TempRepo()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repoql-protocol-tests-{Guid.NewGuid()}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in tests
            }
        }
    }

    private sealed class TestRepoqlFileWriter : IRepoqlFileWriter
    {
        public TestRepoqlFileWriter(string repoRoot)
        {
            RepoRoot = Path.GetFullPath(repoRoot);
            RepoqlDirectory = RepoqlPaths.GetRepoqlDirectoryPath(RepoRoot);
        }

        public string RepoRoot { get; }

        public string RepoqlDirectory { get; }

        public string? LastRelativePath { get; private set; }

        public string? LastContents { get; private set; }

        public void WriteAllText(string relativePath, string contents)
        {
            LastRelativePath = relativePath;
            LastContents = contents;
        }
    }

    private static string BuildLongRepoRoot()
    {
        var baseRoot = Path.Combine("repoql", "long-path");
        var limit = GetPlatformSocketPathLimit();
        var length = 0;

        while (length < 200)
        {
            var root = Path.Combine(baseRoot, new string('a', length));
            var fullRoot = Path.GetFullPath(root);
            var defaultSocket = RepoqlSocketPathResolver.NormalizeSocketPath(
                RepoqlPaths.GetDefaultSocketPath(fullRoot),
                fullRoot);
            if (defaultSocket.Length >= limit)
                return root;

            length += 10;
        }

        return Path.Combine(baseRoot, new string('a', length));
    }

    private static int GetPlatformSocketPathLimit()
        => OperatingSystem.IsMacOS() ? 104 : 108;
}
