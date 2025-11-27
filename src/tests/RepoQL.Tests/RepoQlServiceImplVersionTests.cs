using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Data.DuckDB;

namespace RepoQL.Tests;

/// <summary>
/// Tests for schema version checking in DuckDbGraphStore.
/// Schema versioning is handled by DuckDbGraphStore.EnsureSchema() which checks a 'repoql.version'
/// key in repo_metadata and drops/recreates all tables if the version changes.
/// </summary>
internal class SchemaVersionTests
{
    [Test]
    public Task EnsureSchema_SetsVersionOnFreshDatabase()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.duckdb");
        try
        {
            using var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: true, logger: NullLogger<DuckDbGraphStore>.Instance);
            store.EnsureSchema();

            var row = store.RawQuery("SELECT value FROM repo_metadata WHERE key=?", "repoql.version").FirstOrDefault();
            row.Should().NotBeNull();
            row!["value"].Should().NotBeNull();
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            var walPath = dbPath + ".wal";
            if (File.Exists(walPath)) File.Delete(walPath);
        }

        return Task.CompletedTask;
    }

    [Test]
    public Task EnsureSchema_DropsTablesWhenVersionChanges()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.duckdb");
        try
        {
            // Create initial schema with a fake old version
            using (var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: true, logger: NullLogger<DuckDbGraphStore>.Instance))
            {
                store.EnsureSchema();

                // Insert a test artifact to verify it gets dropped
                store.RawQuery("INSERT INTO artifact(id, digest, byte_size) VALUES (?, ?, ?)",
                    Guid.NewGuid(), "sha256:test", 100).FirstOrDefault();

                // Change the version to simulate an old schema
                store.RawQuery("UPDATE repo_metadata SET value = ? WHERE key = ?", "old-version", "repoql.version").FirstOrDefault();

                var artifactCount = store.RawQuery("SELECT COUNT(*) as cnt FROM artifact").FirstOrDefault();
                ((long)artifactCount!["cnt"]).Should().Be(1);
            }

            // Reopen - EnsureSchema should detect version mismatch and drop everything
            using (var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: true, logger: NullLogger<DuckDbGraphStore>.Instance))
            {
                store.EnsureSchema();

                // Artifact should be gone (table was dropped and recreated)
                var artifactCount = store.RawQuery("SELECT COUNT(*) as cnt FROM artifact").FirstOrDefault();
                ((long)artifactCount!["cnt"]).Should().Be(0);

                // Version should be updated
                var row = store.RawQuery("SELECT value FROM repo_metadata WHERE key=?", "repoql.version").FirstOrDefault();
                row.Should().NotBeNull();
                row!["value"].Should().NotBe("old-version");
            }
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            var walPath = dbPath + ".wal";
            if (File.Exists(walPath)) File.Delete(walPath);
        }

        return Task.CompletedTask;
    }

    [Test]
    public Task EnsureSchema_PreservesDataWhenVersionMatches()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.duckdb");
        try
        {
            var testId = Guid.NewGuid();

            // Create schema and insert data
            using (var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: true, logger: NullLogger<DuckDbGraphStore>.Instance))
            {
                store.EnsureSchema();

                store.RawQuery("INSERT INTO artifact(id, digest, byte_size) VALUES (?, ?, ?)",
                    testId, "sha256:preserve", 200).FirstOrDefault();
            }

            // Reopen - EnsureSchema should NOT drop because version matches
            using (var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: true, logger: NullLogger<DuckDbGraphStore>.Instance))
            {
                store.EnsureSchema();

                // Artifact should still exist
                var row = store.RawQuery("SELECT digest FROM artifact WHERE id = ?", testId).FirstOrDefault();
                row.Should().NotBeNull();
                row!["digest"].Should().Be("sha256:preserve");
            }
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            var walPath = dbPath + ".wal";
            if (File.Exists(walPath)) File.Delete(walPath);
        }

        return Task.CompletedTask;
    }
}
