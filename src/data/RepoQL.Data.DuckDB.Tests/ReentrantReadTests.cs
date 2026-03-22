using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Data;
using RepoQL.Contracts.Models;

namespace RepoQL.Data.DuckDB.Tests;

public class ReentrantReadTests
{
    [Test]
    [DisplayName("Read inside WriteTransaction completes without deadlock")]
    public void Read_InsideWriteTransaction_DoesNotDeadlock()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        SeedDocument(db);

        // This simulates the UDF callback scenario: a WriteTransaction holds the exclusive section,
        // and inside it we call Read which re-enters EnterExclusiveSection.
        // Before the reentrant fix, this would self-deadlock (Monitor.Wait on a lock we already hold).
        IReadOnlyList<string>? innerResults = null;

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var act = () => db.WriteTransaction((conn, tx) =>
        {
            innerResults = db.Read(
                "SELECT uri FROM node WHERE kind = 'document'",
                r => r.GetString(0),
                cts.Token);
        });

        act.Should().NotThrow();
        innerResults.Should().NotBeNull();
        innerResults.Should().HaveCount(1);
        innerResults![0].Should().Contain("reentrant-test");
    }

    [Test]
    [DisplayName("ReadScalar inside WriteTransaction completes without deadlock")]
    public void ReadScalar_InsideWriteTransaction_DoesNotDeadlock()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        SeedDocument(db);

        long? count = null;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var act = () => db.WriteTransaction((conn, tx) =>
        {
            count = db.ReadScalar<long>("SELECT COUNT(*) FROM node", cts.Token);
        });

        act.Should().NotThrow();
        count.Should().BeGreaterThan(0);
    }

    [Test]
    [DisplayName("IReentrantReader.Read bypasses lock and uses reentrant connection")]
    public void IReentrantReader_Read_BypassesLock()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        SeedDocument(db);

        IReentrantReader reader = db;
        IReadOnlyList<string>? innerResults = null;

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var act = () => db.WriteTransaction((conn, tx) =>
        {
            innerResults = reader.Read(
                "SELECT uri FROM node WHERE kind = 'document'",
                r => r.GetString(0),
                cts.Token);
        });

        act.Should().NotThrow();
        innerResults.Should().NotBeNull();
        innerResults.Should().HaveCount(1);
    }

    [Test]
    [DisplayName("IReentrantReader.ReadScalar bypasses lock and uses reentrant connection")]
    public void IReentrantReader_ReadScalar_BypassesLock()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        SeedDocument(db);

        IReentrantReader reader = db;
        long? count = null;

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var act = () => db.WriteTransaction((conn, tx) =>
        {
            count = reader.ReadScalar<long>("SELECT COUNT(*) FROM node", cts.Token);
        });

        act.Should().NotThrow();
        count.Should().BeGreaterThan(0);
    }

    [Test]
    [DisplayName("Concurrent reentrant reads from multiple threads are serialized")]
    public void ConcurrentReentrantReads_AreSerializedByLock()
    {
        using var db = TestServiceCollectionExtensions.CreateTestDataStore();
        SeedDocument(db);

        IReentrantReader reader = db;
        var errors = new List<Exception>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Simulate multiple UDF callbacks hitting IReentrantReader concurrently
        db.WriteTransaction((conn, tx) =>
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                try
                {
                    var result = reader.Read(
                        "SELECT COUNT(*) FROM node",
                        r => r.GetInt64(0),
                        cts.Token);
                    result.Should().HaveCount(1);
                }
                catch (Exception ex)
                {
                    lock (errors) errors.Add(ex);
                }
            }, cts.Token)).ToArray();

            Task.WaitAll(tasks, cts.Token);
        });

        errors.Should().BeEmpty();
    }

    private static void SeedDocument(DuckDbDataStore db)
    {
        var uri = RepoUri.Parse("file:///test/reentrant-test.cs")!;
        var artifactId = Guid.NewGuid();
        db.IndexArtifact(uri, new ParsedArtifact
        {
            Artifact = new RepoQL.Contracts.Models.Artifact
            {
                Id = artifactId,
                Digest = $"sha256:{Guid.NewGuid():N}",
                Size = 100,
                MediaType = SemanticMediaType.Parse("text/x-csharp"),
                Text = "class ReentrantTest { }"
            },
            DocumentNode = new Node
            {
                Id = Guid.NewGuid(),
                Kind = "document",
                Uri = uri,
                ArtifactId = artifactId,
                Headline = "Reentrant Test File"
            },
            Children = [],
            Spans = [],
            Edges = []
        });
    }
}
