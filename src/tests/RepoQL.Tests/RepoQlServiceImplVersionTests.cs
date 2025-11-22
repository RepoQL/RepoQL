using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts.Data;
using RepoQL.Data.DuckDB;
using RepoQL.Indexing.Hosting;

namespace RepoQL.Tests;

internal class RepoQlServiceImplVersionTests
{
    [Test]
    public async Task EnsureVersionMetadata_InsertsWhenMissing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.duckdb");
        using (var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: true, logger: NullLogger<DuckDbGraphStore>.Instance))
        {
            store.EnsureSchema();

            var coordinator = new FakeCoordinator();
            var writer = new FakeWriter();

            await RepoQlServiceImpl.EnsureVersionMetadataAsync(store, coordinator, writer, "1.2.3", NullLogger<RepoQlServiceImpl>.Instance, CancellationToken.None);

            var row = store.RawQuery("SELECT value FROM repo_metadata WHERE key=?", "repoql.version").FirstOrDefault();
            row.Should().NotBeNull();
            row!["value"].Should().Be("1.2.3");
            coordinator.ReindexCalls.Should().Be(0);
            writer.FlushCalls.Should().Be(0);
        }

        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task EnsureVersionMetadata_ReindexesAndUpdatesWhenChanged()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.duckdb");
        using (var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: true, logger: NullLogger<DuckDbGraphStore>.Instance))
        {
            store.EnsureSchema();
            store.RawQuery("INSERT INTO repo_metadata(key, value) VALUES ('repoql.version', 'old-version')").FirstOrDefault();

            var coordinator = new FakeCoordinator();
            var writer = new FakeWriter();

            await RepoQlServiceImpl.EnsureVersionMetadataAsync(store, coordinator, writer, "new-version", NullLogger<RepoQlServiceImpl>.Instance, CancellationToken.None);

            var row = store.RawQuery("SELECT value FROM repo_metadata WHERE key=?", "repoql.version").FirstOrDefault();
            row.Should().NotBeNull();
            row!["value"].Should().Be("new-version");
            coordinator.ReindexCalls.Should().Be(1);
            writer.FlushCalls.Should().Be(1);
        }

        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }

    private sealed class FakeCoordinator : IIndexingCoordinator
    {
        public int ReindexCalls { get; private set; }

        public bool IsReindexing => false;

        public PipelineStatusSnapshot GetPipelineStatus() => throw new NotImplementedException();

        public Task WaitForPipelineAsync(IReadOnlyCollection<CoordinatorPipelineStage> stages, bool waitAll, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task WaitForIdleAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

        public async IAsyncEnumerable<ReindexProgressSnapshot> ReindexAsync(ReindexRequestOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReindexCalls++;
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class FakeWriter : IDatabaseWriter
    {
        public int FlushCalls { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask EnqueueAsync(WriteOperation operation, CancellationToken ct = default) => throw new NotImplementedException();

        public ValueTask<CommitResult> EnqueueAndWaitAsync(WriteOperation operation, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<FlushResult> FlushAsync(CancellationToken ct = default)
        {
            FlushCalls++;
            return Task.FromResult(new FlushResult());
        }

        public WriterStatus GetStatus() => new();

        public int QueueCapacity => 0;
    }
}
