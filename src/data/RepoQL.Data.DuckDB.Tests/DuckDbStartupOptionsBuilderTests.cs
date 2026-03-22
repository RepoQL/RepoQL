using AwesomeAssertions;
using RepoQL.Contracts.Configuration;
using RepoQL.Data.DuckDB;

namespace RepoQL.Data.DuckDB.Tests;

public class DuckDbStartupOptionsBuilderTests
{
    [Test]
    public void Build_FallsBackWhenMemoryLimitInvalid()
    {
        var settings = new RepoQlConfig.DuckDbSettings
        {
            MemoryLimit = "not-a-number"
        };

        var options = DuckDbStartupOptionsBuilder.Build(null, settings);

        options.InvalidEnvironmentVariables.Should().ContainSingle(issue => issue.Name == "DUCKDB_MEMORY_LIMIT");
        options.MemoryLimit.Should().NotBe("NOT-A-NUMBER");
    }

    [Test]
    public void Build_FallsBackWhenThreadsInvalid()
    {
        var settings = new RepoQlConfig.DuckDbSettings
        {
            Threads = -5
        };

        var options = DuckDbStartupOptionsBuilder.Build(null, settings);

        options.InvalidEnvironmentVariables.Should().ContainSingle(issue => issue.Name == "DUCKDB_THREADS");
        options.Threads.Should().BeGreaterThan(0);
    }

    [Test]
    public void Build_DefaultsTempDirectoryNextToDatabase()
    {
        using var temp = new TempDir();
        var repoqlDir = Path.Combine(temp.Path, ".repoql");
        var dbPath = Path.Combine(repoqlDir, "index.duckdb");

        var options = DuckDbStartupOptionsBuilder.Build(dbPath, new RepoQlConfig.DuckDbSettings());

        options.TempDirectory.Should().Be(Path.Combine(repoqlDir, "temp"));
    }

    [Test]
    public void Build_DefaultsReadPoolSize()
    {
        var options = DuckDbStartupOptionsBuilder.Build(null, new RepoQlConfig.DuckDbSettings());

        options.ReadPoolSize.Should().Be(2);
    }

    [Test]
    public void Build_FallsBackWhenReadPoolSizeInvalid()
    {
        var settings = new RepoQlConfig.DuckDbSettings
        {
            ReadPoolSize = 99
        };

        var options = DuckDbStartupOptionsBuilder.Build(null, settings);

        options.InvalidEnvironmentVariables.Should().ContainSingle(issue => issue.Name == "DUCKDB_READ_POOL_SIZE");
        options.ReadPoolSize.Should().Be(2);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repoql-duckdb-tests-{Guid.NewGuid():N}");
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
                // ignore cleanup failures
            }
        }
    }
}
