using System.Diagnostics.Metrics;
using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Core;
using RepoQL.Core.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;
using RepoQL.Tests.Scaffolding;

namespace RepoQL.Tests;

internal class FrontmatterParsingTests
{
    [Test]
    public async Task MarkdownFrontmatter_IsFlattenedIntoDocumentProps()
    {
        // Arrange
        var fs = new MemoryFileSystem("repo");
        var content = """
        ---
        description: Test document
        documentationCategory: example
        tags: [markdown, md, text/markdown]
        ---

        # Title
        """;
        var uri = RepoUri.Parse("mem://repo/docs/fm.md");

        var meter = new Meter("RepoQL.Tests.Frontmatter");
        var metrics = new IndexingMetrics();
        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-fm-{Guid.NewGuid():N}.duckdb");
        using var store = new DuckDbGraphStore(dbPath, enableExtensions: false, registerUdfs: false);
        var classifier = new TestClassifier();
        var hasher = new XxHasher();
        var filter = new FileSystem.NoOpUriFilter();

        var fsRegistry = new FileSystem.FileSystemRegistry([fs]);
        var hub = new FileSystem.MultiFileSystem(fsRegistry, [fs]);
        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);
        var factory = new DuckDBConnectionFactory($"Data Source={dbPath}");
        await using var writer = new SingleThreadedDatabaseWriter(factory, new ConsoleLogger<SingleThreadedDatabaseWriter>());
        await writer.StartAsync(CancellationToken.None);
        await using var indexer = new RepositoryIndexer(metrics, meter, hub, store, classifier, formatRegistry, workspace, filter, hasher, writer, analysisWriter: new AnnotationResultWriter(store));

        // Act
        var errors = new List<Exception>();
        using var errSub = indexer.Subscribe(new TestObserver(ex =>
        {
            errors.Add(ex);
            Console.WriteLine($"Indexer error: {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");
        }, ev =>
        {
            switch (ev)
            {
                case IRepositoryIndexer.ItemClassifiedEvent c:
                    Console.WriteLine($"Classified: {c.CurrentUri}");
                    break;
                case IRepositoryIndexer.ItemIndexedEvent i:
                    Console.WriteLine($"Indexed: {i.CurrentUri}");
                    break;
                case IRepositoryIndexer.ItemDiscoveredEvent d:
                    Console.WriteLine($"Discovered: {d.CurrentUri}");
                    break;
                case IRepositoryIndexer.ItemUpdatedEvent u:
                    Console.WriteLine($"Updated: {u.CurrentUri}");
                    break;
                case IRepositoryIndexer.ItemDeletedEvent de:
                    Console.WriteLine($"Deleted: {de.CurrentUri}");
                    break;
                case IRepositoryIndexer.ItemMovedEvent m:
                    Console.WriteLine($"Moved: {m.PreviousUri} -> {m.CurrentUri}");
                    break;
            }
        }));
        await indexer.StartAsync(CancellationToken.None);
        // Add the file AFTER start to avoid enumeration/dedup race
        fs.AddOrUpdateText("docs/fm.md", content);
        // Explicitly queue and wait for the indexer to become idle
        await indexer.QueueForIndexingAsync([uri]);
        await indexer.WaitForIdle();
        var flush = await writer.FlushAsync();
        var status = writer.GetStatus();
        Console.WriteLine($"Writer status: processed={status.TotalProcessed}, pending={status.PendingCount}, flushed={flush.OperationsFlushed}");

        // Assert
        if (errors.Count > 0)
        {
            Console.WriteLine("Indexer errors:");
            foreach (var e in errors) Console.WriteLine(e);
        }
        // Wait for the writer to make the doc visible (poll up to ~2s)
        var deadline = DateTime.UtcNow.AddSeconds(2);
        Node? doc = null;
        do
        {
            doc = store.GetDocumentByUri(uri);
            if (doc is not null) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        if (doc is null)
        {
            Console.WriteLine("DB identity (DuckDB PRAGMA database_list):");
            foreach (var row in store.RawQuery("PRAGMA database_list"))
            {
                var name = row.TryGetValue("name", out var v1) ? v1?.ToString() : "";
                var file = row.TryGetValue("file", out var v2) ? v2?.ToString() : "";
                Console.WriteLine($" - {name}: {file}");
            }

            Console.WriteLine("artifact rows:");
            foreach (var r in store.RawQuery("SELECT id, digest, byte_size, media_type, LENGTH(COALESCE(text_content,'')) AS text_len FROM artifact"))
                Console.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key}={kv.Value}")));

            Console.WriteLine("node rows:");
            foreach (var r in store.RawQuery("SELECT kind, uri, container_uri_lowercase, artifact_id, span_id, created_at FROM node"))
                Console.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key}={kv.Value}")));

            Console.WriteLine("span rows:");
            foreach (var r in store.RawQuery("SELECT id, document_id, start_line, end_line, start_byte, end_byte FROM span"))
                Console.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key}={kv.Value}")));

            Console.WriteLine("edge rows:");
            foreach (var r in store.RawQuery("SELECT id, source_node_id, destination_node_id, type, is_composition, scope_document_id FROM edge"))
                Console.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key}={kv.Value}")));

            Console.WriteLine("annotation rows:");
            foreach (var r in store.RawQuery("SELECT id, kind, severity, source, scope_document_id FROM annotation"))
                Console.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        doc.Should().NotBeNull();
        doc.Props!["description"]!.GetValue<string>().Should().Be("Test document");
        doc.Props!["documentationCategory"]!.GetValue<string>().Should().Be("example");
        var tags = doc.Props!["tags"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        tags.Should().BeEquivalentTo(["markdown", "md", "text/markdown"]);

        await indexer.StopAsync(CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);
        try { File.Delete(dbPath); } catch { }
    }

    private sealed class TestClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
    }

    private static (IFormatRegistry Registry, IAnalysisWorkspace Workspace) CreateFormats(IMultiFileSystem hub, IFileClassifier classifier, IHasher hasher)
    {
        var markdownLoader = new MarkdownLoader();
        var markdownAnalyzer = new MarkdownAnalyzer();
        var mermaidLoader = new MermaidLoader();
        var mermaidAnalyzer = new MermaidAnalyzer();
        var plainLoader = new PlainTextLoader();
        var plainAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));

        var descriptors = new[]
        {
            new FormatDescriptor(SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"), markdownLoader, markdownAnalyzer, markdownLoader,
                ["markdown"]),
            new FormatDescriptor(SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"), mermaidLoader, mermaidAnalyzer, mermaidLoader,
                ["mermaid", "mmd"]),
            new FormatDescriptor(SemanticMediaType.Create("text", "plain").WithKind("plain.document"), plainLoader, plainAnalyzer, plainLoader)
        };

        var registry = new FormatRegistry(descriptors);
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        return (registry, workspace);
    }
}
