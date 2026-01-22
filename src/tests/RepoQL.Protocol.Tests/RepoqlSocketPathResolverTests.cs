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
}
