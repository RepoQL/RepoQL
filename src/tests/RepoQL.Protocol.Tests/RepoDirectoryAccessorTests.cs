using AwesomeAssertions;
using TUnit.Assertions.Extensions;

namespace RepoQL.Protocol.Tests;

public class RepoDirectoryAccessorTests
{
    [Test]
    public void ResolveSocketPath_ReturnsDefaultPathWhenMappingMissing()
    {
        using var temp = new TempRepo();
        using var accessor = new RepoDirectoryAccessor(temp.Path);

        var path = accessor.ResolveSocketPath();

        path.Should().Be(Path.Combine(accessor.RepoqlDirectory, "repoql.sock"));
    }

    [Test]
    public void ResolveSocketPath_UsesMappingWhenPresent()
    {
        using var temp = new TempRepo();
        var repoqlDir = Path.Combine(temp.Path, ".repoql");
        Directory.CreateDirectory(repoqlDir);
        File.WriteAllText(Path.Combine(repoqlDir, "socket.path"), "/tmp/repoql/custom.sock");

        using var accessor = new RepoDirectoryAccessor(temp.Path);

        accessor.ResolveSocketPath().Should().Be("/tmp/repoql/custom.sock");
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
