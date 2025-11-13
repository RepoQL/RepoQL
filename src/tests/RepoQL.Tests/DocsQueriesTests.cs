using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;
using RepoQL.Core;
using RepoQL.Testing.Scaffolding;
using RepoQL.FileSystem.Embedded;
using RepoQL.Indexing.FileSystems;

namespace RepoQL.Tests;

internal class DocsQueriesTests
{
    [Test]
    public async Task EmbeddedDocs_AreQueryable_ViaQuickstartPatterns()
    {
        var asm = typeof(Documentation.DocumentationMarker).Assembly;
        var embedStore = new EmbeddedStore(asm);

        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.EnableWatching = false;
            options.RunFullScanOnStartup = true;
            options.DeleteDatabaseOnDispose = true;
            ConfigureFormats(options);
            options.AdditionalMounts.Add(
                CompositeFileSystemMount.ForScheme(
                    id: "embedded-docs",
                    fileSystem: embedStore,
                    scheme: embedStore.Scheme,
                    includeInEnumeration: true));
        });

        await repo.WaitForIdleAsync();

        var store = repo.Store;

        var list = store.RawQuery(
            """
            SELECT repository_uri_file_name(uri) AS file_name,
                   uri,
                   properties->>'documentationCategory' AS category,
                   properties->>'description'           AS description
              FROM node
             WHERE kind='document'
               AND lower(uri) LIKE 'embed://%'
             ORDER BY lower(file_name)
            """).ToArray();

        list.Should().NotBeEmpty("embedded docs are indexed and queryable");
        var quickstart = list.FirstOrDefault(r => string.Equals(r["file_name"]?.ToString(), "quickstart.md", StringComparison.OrdinalIgnoreCase));
        var docUri = (quickstart != null ? quickstart["uri"] : list.First()["uri"])!.ToString();

        var schemaUri = docUri;

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

        var full = store.RawQuery(
            """
            SELECT a.text_content
              FROM node n JOIN artifact a ON a.id = n.artifact_id
             WHERE n.uri = ?
            """,
            schemaUri);
        full.Should().NotBeEmpty("content should be present for documents");
        (full.First()["text_content"]?.ToString() ?? string.Empty).Length.Should().BeGreaterThan(0);

        var snippet = store.RawQuery(
            """
            SELECT line_number, text, is_focus
              FROM snippet(? || '#line=1', 3)
            """,
            schemaUri);
        snippet.Should().NotBeEmpty("snippet should return a small window");

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
    }

    private static void ConfigureFormats(IndexedRepoOptions options)
    {
        var markdownLoader = new MarkdownLoader();
        var markdownAnalyzer = new MarkdownAnalyzer();
        var mermaidLoader = new MermaidLoader();
        var mermaidAnalyzer = new MermaidAnalyzer();
        var plainLoader = new PlainTextLoader();
        var plainAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));

        options.AddFormat(new FormatDescriptor(
            SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"),
            markdownLoader,
            markdownAnalyzer,
            markdownLoader,
            ["markdown", "md"]));

        options.AddFormat(new FormatDescriptor(
            SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"),
            mermaidLoader,
            mermaidAnalyzer,
            mermaidLoader,
            ["mermaid", "mmd"]));

        options.AddFormat(new FormatDescriptor(
            SemanticMediaType.Create("text", "plain").WithKind("plain.document"),
            plainLoader,
            plainAnalyzer,
            plainLoader,
            ["txt", "text"]));
    }
}
