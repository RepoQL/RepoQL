using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem.Embedded;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;
using RepoQL.Testing;

namespace RepoQL.Tests;

internal class DocsQueriesTests
{
    [Test]
    public async Task EmbeddedDocs_AreQueryable_ViaQuickstartPatterns()
    {
        // Arrange: index embedded docs from RepoQL.Data assembly into a temp DuckDB file
        var asm = typeof(Documentation.DocumentationMarker).Assembly;
        var embed = new EmbeddedStore(asm);
        var registry = new FileSystem.FileSystemRegistry([embed]);
        var hub = new FileSystem.MultiFileSystem(registry, [embed]);

        var dbPath = Path.Combine(Path.GetTempPath(), $"repoql-docs-{Guid.NewGuid():N}.duckdb");
        await using var writer = new SingleThreadedDatabaseWriter(new DuckDBConnectionFactory($"Data Source={dbPath}"));
        await writer.StartAsync(CancellationToken.None);
        
        // Read store with UDFs/macros enabled for queries
        using var store = new DuckDbGraphStore(dbPath);

        var classifier = new TestClassifier();
        var hasher = new XxHasher();
        var filter = new FileSystem.NoOpUriFilter();

        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);

        await using var indexer = new RepositoryIndexer(hub, store, classifier, formatRegistry, workspace, filter, hasher, writer, analysisWriter: new AnnotationResultWriter(store));
        var errors = new List<Exception>();
        using var sub = indexer.Subscribe(new TestObserver(ex => {
            errors.Add(ex);
            Console.WriteLine($"Indexer error: {ex}");
        }, ev =>
        {
            switch (ev)
            {
                case IRepositoryIndexer.ItemDiscoveredEvent d: Console.WriteLine($"Discovered: {d.CurrentUri}"); break;
                case IRepositoryIndexer.ItemClassifiedEvent c: Console.WriteLine($"Classified: {c.CurrentUri}"); break;
                case IRepositoryIndexer.ItemIndexedEvent i: Console.WriteLine($"Indexed: {i.CurrentUri}"); break;
            }
        }));
        await indexer.StartAsync(CancellationToken.None);

        // Wait for enumerate+parse+write to finish and commit
        await indexer.WaitForIdle();

        // 1) List embedded docs (no restrictive subpath filter)
        var list = store.RawQuery(
            """
            SELECT repository_uri_file_name(uri) AS file_name,
                                  uri,
                                  properties->>'documentationCategory' AS category,
                                  properties->>'description'           AS description
                           FROM node
                           WHERE kind='document' AND lower(uri) LIKE 'embed://%'
                           ORDER BY lower(file_name)
            """).ToArray();
        if (list.Length == 0)
        {
            var status = writer.GetStatus();
            Console.WriteLine($"Writer status: processed={status.TotalProcessed}, pending={status.PendingCount}");
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
            foreach (var r in store.RawQuery("SELECT kind, uri, container_uri_lowercase, artifact_id FROM node"))
                Console.WriteLine(string.Join(", ", r.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        list.Should().NotBeEmpty("embedded docs are indexed and queryable");
        // Prefer quickstart.md when present; fallback to first result
        var quickstart = list.FirstOrDefault(r => string.Equals(r["file_name"]?.ToString(), "quickstart.md", StringComparison.OrdinalIgnoreCase));
        var docUri = (quickstart != null ? quickstart["uri"] : list.First()["uri"])!.ToString();

        // Grab one canonical doc URI for subsequent queries
        var schemaUri = docUri;

        // 2) Media type filter: ensure at least one embedded document is markdown
        var markdownDocs = store.RawQuery(
            """
            SELECT n.uri
                          FROM node n
                          JOIN artifact a ON a.id = n.artifact_id
                          WHERE n.kind='document'
                            AND lower(n.uri) LIKE 'embed://%'
                            AND lower(a.media_type) LIKE '%text/markdown%'
            """);
        markdownDocs.Should().NotBeEmpty("at least one embedded doc should be markdown");

        // 3) Read full content via JOIN artifact
        var full = store.RawQuery(
            """
            SELECT a.text_content
                          FROM node n JOIN artifact a ON a.id = n.artifact_id
                          WHERE n.uri = ?
            """,
            schemaUri);
        full.Should().NotBeEmpty("content should be present for documents");
        (full.First()["text_content"]?.ToString() ?? string.Empty).Length.Should().BeGreaterThan(0);

        // 4) Focused snippet for the first lines
        var snippet = store.RawQuery(
            """
            SELECT line_number, text, is_focus
                          FROM snippet(? || '#line=1', 3)
            """,
            schemaUri);
        snippet.Should().NotBeEmpty("snippet should return a small window");

        // 5) Structural navigation: headings exist for Schema.md
        var headings = store.RawQuery(
            """
            SELECT child.properties->>'text' AS heading
                          FROM node doc
                          JOIN edge e     ON e.source_node_id = doc.id AND e.is_composition = TRUE AND e.type = 'HAS_PART'
                          JOIN node child ON child.id = e.destination_node_id AND child.kind = 'md_heading'
                          WHERE doc.uri = ?
                          ORDER BY e.ordinal
            """,
            schemaUri);
        headings.Should().NotBeEmpty("schema doc should have headings");

        // Cleanup
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
