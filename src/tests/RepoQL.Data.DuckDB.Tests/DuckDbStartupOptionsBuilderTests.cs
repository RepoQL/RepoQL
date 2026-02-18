using AwesomeAssertions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Data.DuckDB.Tests;

public class DuckDbStartupOptionsBuilderTests
{
    [Test]
    public void Build_FallsBackWhenMemoryLimitInvalid()
    {
        var original = Environment.GetEnvironmentVariable("DUCKDB_MEMORY_LIMIT");
        try
        {
            Environment.SetEnvironmentVariable("DUCKDB_MEMORY_LIMIT", "not-a-number");

            var options = DuckDbStartupOptionsBuilder.Build(null);

            options.InvalidEnvironmentVariables.Should().ContainSingle(issue => issue.Name == "DUCKDB_MEMORY_LIMIT");
            options.MemoryLimit.Should().NotBe("NOT-A-NUMBER");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUCKDB_MEMORY_LIMIT", original);
        }
    }

    [Test]
    public void Build_FallsBackWhenThreadsInvalid()
    {
        var original = Environment.GetEnvironmentVariable("DUCKDB_THREADS");
        try
        {
            Environment.SetEnvironmentVariable("DUCKDB_THREADS", "-5");

            var options = DuckDbStartupOptionsBuilder.Build(null);

            options.InvalidEnvironmentVariables.Should().ContainSingle(issue => issue.Name == "DUCKDB_THREADS");
            options.Threads.Should().BeGreaterThan(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUCKDB_THREADS", original);
        }
    }

    [Test]
    public void Build_DefaultsTempDirectoryNextToDatabase()
    {
        var original = Environment.GetEnvironmentVariable("DUCKDB_TEMP_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("DUCKDB_TEMP_DIRECTORY", null);
            using var temp = new TempDir();
            var repoqlDir = Path.Combine(temp.Path, ".repoql");
            var dbPath = Path.Combine(repoqlDir, "index.duckdb");

            var options = DuckDbStartupOptionsBuilder.Build(dbPath);

            options.TempDirectory.Should().Be(Path.Combine(repoqlDir, "temp"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUCKDB_TEMP_DIRECTORY", original);
        }
    }

    [Test]
    public void Build_DefaultsReadPoolSize()
    {
        var original = Environment.GetEnvironmentVariable("DUCKDB_READ_POOL_SIZE");
        try
        {
            Environment.SetEnvironmentVariable("DUCKDB_READ_POOL_SIZE", null);

            var options = DuckDbStartupOptionsBuilder.Build(null);

            options.ReadPoolSize.Should().Be(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUCKDB_READ_POOL_SIZE", original);
        }
    }

    [Test]
    public void Build_FallsBackWhenReadPoolSizeInvalid()
    {
        var original = Environment.GetEnvironmentVariable("DUCKDB_READ_POOL_SIZE");
        try
        {
            Environment.SetEnvironmentVariable("DUCKDB_READ_POOL_SIZE", "99");

            var options = DuckDbStartupOptionsBuilder.Build(null);

            options.InvalidEnvironmentVariables.Should().ContainSingle(issue => issue.Name == "DUCKDB_READ_POOL_SIZE");
            options.ReadPoolSize.Should().Be(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DUCKDB_READ_POOL_SIZE", original);
        }
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
