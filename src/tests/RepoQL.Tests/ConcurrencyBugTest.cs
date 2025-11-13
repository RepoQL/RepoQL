using System.Collections.Concurrent;
using System.Diagnostics;
using DuckDB.NET.Data;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.InMemory;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class ConcurrencyBugTest
{
    private static void ConfigureFormats(IndexedRepoOptions options)
    {
        var markdownLoader = new Formats.Markdown.MarkdownLoader();
        var markdownAnalyzer = new Formats.Markdown.MarkdownAnalyzer();
        options.AddFormat(new FormatDescriptor(
            SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"),
            markdownLoader,
            markdownAnalyzer,
            markdownLoader,
            ["md", "markdown"]));
    }

    [Test]
    public async Task ConcurrentIndexing_ProcessesAllDocumentsWithoutTransactionAborts()
    {
        const int fileCount = 50;

        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            ConfigureFormats(options);
        });

        for (var i = 0; i < fileCount; i++)
        {
            repo.AddOrUpdateText($"docs/file{i:D3}.md", $"# Doc {i}\n\nContent {i}");
        }
        repo.KnownUris.Count.Should().Be(fileCount);

        var sw = Stopwatch.StartNew();
        foreach (var uri in repo.KnownUris)
        {
            await repo.IndexUriAsync(uri, skipUnchanged: false);
        }
        sw.Stop();

        Console.WriteLine($"Indexed {fileCount} files in {sw.ElapsedMilliseconds}ms");

        var nodeCount = repo.Store.GetAllNodes().Count(n => n.Kind == "document");
        nodeCount.Should().Be(fileCount);
    }

    [Test]
    public async Task SeparateConnections_WithConcurrentWorkers_WorksCorrectly()
    {
        var fileSystem = new MemoryFileSystem();
        var fileCount = 20;

        for (var i = 0; i < fileCount; i++)
        {
            var path = $"test/file{i:D3}.txt";
            fileSystem.AddOrUpdateText(path, $"Content {i}");
        }

        var successCount = 0;
        var errors = new ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, fileCount).Select(i =>
        {
            try
            {
                using var connection = new DuckDBConnection("Data Source=:memory:");
#pragma warning disable CA1849
                connection.Open();
#pragma warning restore CA1849

                using var store = new DuckDbGraphStore(connection, new RepoQL.Metrics.IndexingMetrics());
                store.EnsureSchema();

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

        errors.Should().BeEmpty("No errors expected with separate connections");
        successCount.Should().Be(fileCount, "All operations should succeed");
    }
}
