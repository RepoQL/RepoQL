using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using DuckDB.NET.Data;
using AwesomeAssertions;
using JetBrains.Annotations;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Core;
using RepoQL.Core.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Classification;
using RepoQL.FileSystem.InMemory;
using Artifact = RepoQL.Contracts.Models.Artifact;

namespace RepoQL.Tests;

/// <summary>
/// This test demonstrates the catastrophic concurrency bug in RepoQL where
/// multiple threads share a single DuckDBConnection, causing transaction aborts.
/// 
/// This test SHOULD FAIL with the current implementation and PASS after the fix.
/// </summary>
[UsedImplicitly]
internal class ConcurrencyBugTest
{
    private class TestFormatLoader : IFormatLoader, IFormatMaterializer
    {
        private static int _parseCount;
        private static readonly SemanticMediaType PlainMedia = SemanticMediaType
            .Create("text", "plain")
            .WithKind("plain.document");

        public bool Supports(SemanticMediaType mediaType)
        {
            ArgumentNullException.ThrowIfNull(mediaType);
            return string.Equals(mediaType.Kind, PlainMedia.Kind, StringComparison.OrdinalIgnoreCase)
                   || (string.Equals(mediaType.Type, PlainMedia.Type, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(mediaType.Subtype, PlainMedia.Subtype, StringComparison.OrdinalIgnoreCase));
        }

        public Task<bool> CanLoadAsync(DiscoveredArtifact file, CancellationToken cancellationToken = default)
        {
            file.MediaType = PlainMedia;
            return Task.FromResult(true);
        }

        public async Task<DocumentModel> LoadAsync(DiscoveredArtifact file, CancellationToken cancellationToken = default)
        {
            // Simulate some CPU work
#pragma warning disable CA5394
            await Task.Delay(Random.Shared.Next(10, 50), cancellationToken);
#pragma warning restore CA5394

            string text;
            await using (var stream = file.File.CreateReadStream())
            using (var reader = new StreamReader(stream))
            {
                text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DocumentModel(file.RepoUri, file.MediaType ?? PlainMedia, text);
        }

        public Records Materialize(DocumentModel document)
        {
            var fileUri = document.Uri;

            var parseNum = Interlocked.Increment(ref _parseCount);
            Console.WriteLine($"[Parser {Environment.CurrentManagedThreadId}] Parsing file {fileUri} (parse #{parseNum})");

            // Create substantial data to increase chance of overlap
            var docId = Guid.NewGuid();
            var nodes = new List<Node>
            {
                new()
                {
                    Id = docId,
                    Kind = "document",
                    Uri = fileUri,
                    Props = new System.Text.Json.Nodes.JsonObject(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            };

            // Add child nodes to create more database operations
            for (var i = 0; i < 5; i++)
            {
                nodes.Add(new Node
                {
                    Id = Guid.NewGuid(),
                    Kind = $"child_{i}",
                    Uri = null,
                    Props = new System.Text.Json.Nodes.JsonObject(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            var edges = new List<Edge>();
            for (var i = 1; i < nodes.Count; i++)
            {
                edges.Add(new Edge
                {
                    Id = Guid.NewGuid(),
                    SrcId = docId,
                    DstId = nodes[i].Id,
                    Type = "contains",
                    IsComposition = true,
                    Props = new System.Text.Json.Nodes.JsonObject(),
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            // Create artifact for the document
            var artifact = new Artifact
            {
                Id = Guid.NewGuid(),
                Digest = $"hash_{fileUri.AbsolutePath}",
                Size = document.Text.Length,
                MediaType = PlainMedia,
                Text = document.Text
            };

            return new Records
            {
                Artifacts = [artifact],
                Nodes = [.. nodes],
                Spans = [],
                Edges = [.. edges]
            };
        }

    }

    [Test]
    [Timeout(30_000)] // 30 second timeout in milliseconds
    public async Task SharedConnection_WithConcurrentWorkers_CausesTransactionAborts(CancellationToken cancellationToken)
    {
        // Arrange - Single-writer design: writer owns a single connection; reads use separate connections
        // Use a temporary DuckDB file so writer and store see the same database
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-concurrency-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: false);
        store.EnsureSchema();

        // Create in-memory file system with multiple files
        var fileSystem = new MemoryFileSystem();
        var fileCount = 20; // Enough files to guarantee concurrent processing

        for (var i = 0; i < fileCount; i++)
        {
            var path = $"test/file{i:D3}.txt";
            var content = $"Content of file {i}";
            fileSystem.AddOrUpdateText(path, content);
        }

        // Set up indexer components
        var meter = new Meter("RepoQL.Tests.Concurrency");
        var metrics = new IndexingMetrics();
        var classifier = new FileClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter(); // Include all files
        var testLoader = new TestFormatLoader();
        var formatRegistry = new FormatRegistry([
            new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("plain.document"),
                testLoader,
                new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document")),
                testLoader)
        ]);

        // Track errors
        var errors = new ConcurrentBag<Exception>();
        var transactionAbortedErrors = new ConcurrentBag<string>();
        var successfulIndexes = 0;

        // Create indexer with production configuration (3 parsing workers!)
        var registry = new FileSystemRegistry([fileSystem]);
        var hub = new MultiFileSystem(registry, [fileSystem]);
        // Single writer (hosted service lifecycle simulated here)
        var factory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        await using var writer = new SingleThreadedDatabaseWriter(factory);
        await writer.StartAsync(CancellationToken.None);

        var workspace = new AnalysisWorkspace(hub, classifier, hasher, formatRegistry);

        await using var indexer = new RepositoryIndexer(
            metrics,
            meter,
            hub,
            store,
            classifier,
            formatRegistry,
            workspace,
            filter,
            hasher,
            writer);

        // Subscribe to events to track errors
        using var subscription = indexer.Subscribe(new TestObserver(
            onError: ex =>
            {
                errors.Add(ex);
                Console.WriteLine($"[Error] {ex.GetType().Name}: {ex.Message}");

                if (ex.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase) &&
                    ex.Message.Contains("aborted", StringComparison.OrdinalIgnoreCase))
                {
                    transactionAbortedErrors.Add(ex.Message);
                }
            },
            onNext: evt =>
            {
                if (evt is IRepositoryIndexer.ItemIndexedEvent)
                {
                    Interlocked.Increment(ref successfulIndexes);
                    Console.WriteLine($"[Success] Indexed file successfully (total: {successfulIndexes})");
                }
            }
        ));

        // Act - Start indexer and wait for processing
        var sw = Stopwatch.StartNew();
        await indexer.StartAsync(cancellationToken);

        // Wait for indexing to complete or timeout
        var timeout = TimeSpan.FromSeconds(10);
        var waitUntil = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < waitUntil)
        {
            await Task.Delay(100, cancellationToken);

            // Check if we've processed all files or hit errors
            if (successfulIndexes + errors.Count >= fileCount)
            {
                break;
            }
        }

        sw.Stop();
        Console.WriteLine($"Test completed in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"Successful indexes: {successfulIndexes}");
        Console.WriteLine($"Errors: {errors.Count}");
        Console.WriteLine($"Transaction aborted errors: {transactionAbortedErrors.Count}");


        // After the fix, this assertion should be inverted
        transactionAbortedErrors.Should().BeEmpty();
        errors.Count.Should().Be(0);
        successfulIndexes.Should().Be(fileCount);

        // Clean up
        await indexer.StopAsync(cancellationToken);
        await writer.StopAsync(CancellationToken.None);
        await writer.DisposeAsync();
        try { File.Delete(dbPath); } catch { }
    }

    [Test]
    public async Task SeparateConnections_WithConcurrentWorkers_WorksCorrectly()
    {
        // This test shows the CORRECT pattern that should be used
        // Each operation gets its own connection

        var fileSystem = new MemoryFileSystem();
        var fileCount = 20;

        for (var i = 0; i < fileCount; i++)
        {
            var path = $"test/file{i:D3}.txt";
            fileSystem.AddOrUpdateText(path, $"Content {i}");
        }

        var successCount = 0;
        var errors = new ConcurrentBag<Exception>();

        // Simulate concurrent operations with SEPARATE connections
        var tasks = Enumerable.Range(0, fileCount).Select(i =>
        {
            try
            {
                // Each operation gets its OWN connection
                using var connection = new DuckDBConnection("Data Source=:memory:");
#pragma warning disable CA1849
                connection.Open();
#pragma warning restore CA1849

                using var store = new DuckDbGraphStore(connection, enableExtensions: false, registerUdfs: false);
                store.EnsureSchema();

                // Simulate some work
                var node = new Node
                {
                    Id = Guid.NewGuid(),
                    Kind = "document",
                    Uri = RepoUri.Parse($"file:///test/file{i:D3}.txt"),
                    Props = new System.Text.Json.Nodes.JsonObject(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                var saved = store.UpsertNode(node);
                Interlocked.Increment(ref successCount);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            return Task.CompletedTask;
        });

        await Task.WhenAll(tasks);

        // With separate connections, everything should work
        errors.Should().BeEmpty("No errors expected with separate connections");
        successCount.Should().Be(fileCount, "All operations should succeed");
    }

    private class TestObserver(Action<Exception> onError, Action<IndexerEvent> onNext) : IObserver<IndexerEvent>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => onError(error);
        public void OnNext(IndexerEvent value) => onNext(value);
    }
}
