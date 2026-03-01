using System.Globalization;
using AwesomeAssertions;
using FakeItEasy;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Diagnostics;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class QueueObservabilityUdfTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _db;
    private readonly UriRegistry _registry;
    private readonly IIndexingDiagnosticsProvider _diagnosticsProvider;
    private IReadOnlyList<QueuedItemInfo> _queuedItems = Array.Empty<QueuedItemInfo>();
    private IndexingDiagnosticsSnapshot _snapshot = new()
    {
        Status = "idle",
        Epoch = 0,
        HotPathDepth = 0,
        HotPathActive = 0,
        IdlePending = 0,
        IdleActive = 0,
        AnalysisDepth = 0,
        AnalysisActive = 0,
        WriterPending = 0,
        WriterTotal = 0,
        EmbedMode = "None",
        EmbedLastEpoch = 0,
        LastError = null
    };

    public QueueObservabilityUdfTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "repoql-queue-observability-tests", Guid.NewGuid().ToString("N"));
        var repoqlDir = Path.Combine(_repoRoot, ".repoql");
        Directory.CreateDirectory(repoqlDir);

        _registry = new UriRegistry();
        _diagnosticsProvider = A.Fake<IIndexingDiagnosticsProvider>();
        A.CallTo(() => _diagnosticsProvider.GetQueuedItems()).ReturnsLazily(() => _queuedItems);
        A.CallTo(() => _diagnosticsProvider.GetSnapshot()).ReturnsLazily(() => _snapshot);

        var services = new ServiceCollection();
        services.AddSingleton(new RepositoryConfiguration { Path = _repoRoot });
        services.AddSingleton(_registry);
        services.AddSingleton(_diagnosticsProvider);
        services.AddSingleton<IEmbeddingProvider>(new DisabledEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new DisabledLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        _serviceProvider = services.BuildServiceProvider();

        var dbPath = Path.Combine(repoqlDir, "index.duckdb");
        _db = new DuckDbDataStore(path: dbPath, serviceProvider: _serviceProvider);
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
        try
        {
            if (Directory.Exists(_repoRoot))
                Directory.Delete(_repoRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for test temp directory.
        }
    }

    [Test]
    [DisplayName("processing_queue returns queued items")]
    public void ProcessingQueue_ReturnsQueuedItems()
    {
        _queuedItems =
        [
            new QueuedItemInfo
            {
                Uri = "file:///repo/alpha.cs",
                Name = "alpha.cs",
                Stage = "HotPath",
                Status = "processing",
                EnqueuedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
                Epoch = 7,
                MimeType = "text/plain;kind=code.csharp",
                Size = 1234,
                ReadOnly = false
            }
        ];

        var rows = _db.Read(
            "SELECT uri, stage, status, age_seconds, size_bytes, mime_type FROM processing_queue()",
            r => new ProcessingQueueResult(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                ReadInt(r.GetValue(3)),
                ReadLong(r.GetValue(4)),
                r.IsDBNull(5) ? null : r.GetString(5)));

        rows.Should().HaveCount(1);
        rows[0].Uri.Should().Be("file:///repo/alpha.cs");
        rows[0].Stage.Should().Be("HotPath");
        rows[0].Status.Should().Be("processing");
        rows[0].SizeBytes.Should().Be(1234);
        rows[0].MimeType.Should().Be("text/plain;kind=code.csharp");
    }

    [Test]
    [DisplayName("processing_queue computes age_seconds from enqueued_at")]
    public void ProcessingQueue_ComputesAgeSeconds()
    {
        _queuedItems =
        [
            new QueuedItemInfo
            {
                Uri = "file:///repo/age-test.cs",
                Name = "age-test.cs",
                Stage = "Analysis",
                Status = "queued",
                EnqueuedAt = DateTimeOffset.UtcNow.AddSeconds(-65),
                Epoch = 8,
                MimeType = "text/plain",
                Size = 42,
                ReadOnly = false
            }
        ];

        var ageSeconds = _db.Read(
            "SELECT age_seconds FROM processing_queue()",
            r => ReadInt(r.GetValue(0)));

        ageSeconds.Should().HaveCount(1);
        ageSeconds[0].Should().BeGreaterThanOrEqualTo(65);
        ageSeconds[0].Should().BeLessThanOrEqualTo(67);
    }

    [Test]
    [DisplayName("processing_queue returns zero rows when queue is empty")]
    public void ProcessingQueue_EmptyQueue_ReturnsZeroRows()
    {
        _queuedItems = Array.Empty<QueuedItemInfo>();

        var rows = _db.Read(
            "SELECT uri FROM processing_queue()",
            r => r.GetString(0));

        rows.Should().BeEmpty();
    }

    [Test]
    [DisplayName("failed_files macro returns indexing failures and embedding failures")]
    public void FailedFiles_ReturnsFailedAndEmbeddingFailedRows()
    {
        var failedUri = ParseUri("file:///repo/failed.cs");
        var embedFailedUri = ParseUri("file:///repo/embed-failed.cs");

        _registry.TryRegisterDiscovered(failedUri);
        _registry.SetFailed(failedUri, "classification failed");

        _registry.TryRegisterDiscovered(embedFailedUri);
        _registry.SetIndexed(embedFailedUri, 10, new Dictionary<RepoUri, SymbolEntry>());
        _registry.SetEmbeddingFailed(embedFailedUri, "embedding failed");

        var rows = _db.Read(
            "SELECT uri, status, error FROM failed_files() ORDER BY uri",
            r => new FailedFileResult(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2)));

        rows.Should().HaveCount(2);
        rows.Select(r => r.Uri).Should().Contain("file:///repo/failed.cs");
        rows.Select(r => r.Uri).Should().Contain("file:///repo/embed-failed.cs");

        rows.Single(r => r.Uri == "file:///repo/failed.cs").Status.Should().Be("Failed");
        rows.Single(r => r.Uri == "file:///repo/embed-failed.cs").Status.Should().Be("Indexed");
    }

    [Test]
    [DisplayName("failed_files macro includes skipped rows with status Skipped")]
    public void FailedFiles_IncludesSkippedRows()
    {
        var skippedUri = ParseUri("file:///repo/skipped.cs");
        _registry.SetSkipped(skippedUri, "Skipped by user");

        var rows = _db.Read(
            "SELECT uri, status, error FROM failed_files() WHERE uri = 'file:///repo/skipped.cs'",
            r => new FailedFileResult(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2)));

        rows.Should().HaveCount(1);
        rows[0].Status.Should().Be("Skipped");
        rows[0].Error.Should().Be("Skipped by user");
    }

    [Test]
    [DisplayName("system_health returns a single populated health row")]
    public void SystemHealth_ReturnsSingleHealthRow()
    {
        var staleUri = ParseUri("file:///repo/stale.cs");
        var failedUri = ParseUri("file:///repo/failed-health.cs");
        _registry.TryRegisterDiscovered(staleUri);
        _registry.TryRegisterDiscovered(failedUri);
        _registry.SetStale(staleUri);
        _registry.SetFailed(failedUri, "failure");

        _snapshot = new IndexingDiagnosticsSnapshot
        {
            Status = "indexing",
            Epoch = 12,
            HotPathDepth = 2,
            HotPathActive = 1,
            IdlePending = 3,
            IdleActive = 1,
            AnalysisDepth = 4,
            AnalysisActive = 2,
            WriterPending = 0,
            WriterTotal = 0,
            EmbedMode = "Full",
            EmbedLastEpoch = 11,
            LastError = "oops"
        };

        var rows = _db.Read(
            "SELECT status, queue_depth, active_workers, failed_count, stale_count, epoch, last_error, host_memory_mb, db_size_mb, disk_free_mb FROM system_health()",
            r => new SystemHealthResult(
                r.GetString(0),
                ReadInt(r.GetValue(1)),
                ReadInt(r.GetValue(2)),
                ReadInt(r.GetValue(3)),
                ReadInt(r.GetValue(4)),
                ReadLong(r.GetValue(5)),
                r.IsDBNull(6) ? null : r.GetString(6),
                ReadInt(r.GetValue(7)),
                ReadInt(r.GetValue(8)),
                ReadInt(r.GetValue(9))));

        rows.Should().HaveCount(1);
        rows[0].Status.Should().Be("indexing");
        rows[0].QueueDepth.Should().Be(10);
        rows[0].ActiveWorkers.Should().Be(4);
        rows[0].FailedCount.Should().Be(1);
        rows[0].StaleCount.Should().Be(1);
        rows[0].Epoch.Should().Be(12);
        rows[0].LastError.Should().Be("oops");
        rows[0].DbSizeMb.Should().BeGreaterThanOrEqualTo(0);
        rows[0].DiskFreeMb.Should().BeGreaterThan(0);
    }

    [Test]
    [DisplayName("system_health host_memory_mb is positive")]
    public void SystemHealth_HostMemoryIsPositive()
    {
        var hostMemory = _db.Read(
            "SELECT host_memory_mb FROM system_health()",
            r => ReadInt(r.GetValue(0)));

        hostMemory.Should().HaveCount(1);
        hostMemory[0].Should().BeGreaterThan(0);
    }

    private static RepoUri ParseUri(string uri)
        => RepoUri.TryParse(uri, out var parsed)
            ? parsed!
            : throw new InvalidOperationException($"Unable to parse URI '{uri}'.");

    private static int ReadInt(object value)
        => value switch
        {
            int i => i,
            long l => (int)l,
            string s => int.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };

    private static long ReadLong(object value)
        => value switch
        {
            long l => l,
            int i => i,
            string s => long.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };

    private sealed record ProcessingQueueResult(
        string Uri,
        string Stage,
        string Status,
        int AgeSeconds,
        long SizeBytes,
        string? MimeType);

    private sealed record FailedFileResult(
        string Uri,
        string Status,
        string? Error);

    private sealed record SystemHealthResult(
        string Status,
        int QueueDepth,
        int ActiveWorkers,
        int FailedCount,
        int StaleCount,
        long Epoch,
        string? LastError,
        int HostMemoryMb,
        int DbSizeMb,
        int DiskFreeMb);

    private sealed class DisabledEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => false;
        public string Model => "disabled";
        public int Dimension => 384;

        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(null);

        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);

        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private sealed class DisabledLlmProvider : ILlmProvider
    {
        public bool Enabled => false;
        public string Model => "disabled";

        public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult("LLM disabled in tests");

        public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult(new LlmSummaryResult("LLM disabled in tests"));

        public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
            => Task.FromResult("LLM disabled in tests");

        public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }
}
