using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Models;
using RepoQL.Core;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class ReindexingMemoryFsTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [Skip("Flaky")]
    public async Task Reindex_UpdatesExistingDocument_AndReplacesSubtree()
    {
        await using var repo = await CreateRepoAsync();

        var initialContent = """
        # One

        ## Two

        ```txt
        code
        ```
        """;

        var uri = repo.AddOrUpdateText("docs/x.md", initialContent);
        await repo.IndexAsync();

        var nodesBefore = repo.Store.GetAllNodes().ToArray();
        nodesBefore.Count(n => n.Kind == "document").Should().Be(1);
        nodesBefore.Count(n => n.Kind == "md_heading").Should().Be(2);

        repo.AddOrUpdateText("docs/x.md", """
        # Only

        Text
        """);
        await repo.IndexAsync();

        var nodesAfter = repo.Store.GetAllNodes().ToArray();
        nodesAfter.Count(n => n.Kind == "document").Should().Be(1);
        nodesAfter.Count(n => n.Kind == "md_heading").Should().Be(1);
    }

    [Test]
    public async Task Reindex_Unchanged_ShortCircuits_LeavesDocumentUntouched()
    {
        await using var repo = await CreateRepoAsync();

        var content = """
        # A

        Text
        """;
        var uri = repo.AddOrUpdateText("docs/y.md", content);
        await repo.IndexAsync();

        var before = repo.Store.GetDocumentByUri(uri)!;
        var beforeNodes = repo.Store.GetAllNodes().ToArray();

        // Queue same file again and ensure the catalog short-circuits the rewrite
        await repo.IndexAsync(skipUnchanged: true);

        // Allow a short grace window to mirror prior polling behaviour
        var deadline = DateTime.UtcNow.Add(DefaultTimeout);
        Node? after;
        do
        {
            after = repo.Store.GetDocumentByUri(uri);
            if (after is not null)
                break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);
        after.Should().NotBeNull();

        after!.UpdatedAt.Should().Be(before.UpdatedAt);
        after.ArtifactId.Should().Be(before.ArtifactId);

        var nodesAfter = repo.Store.GetAllNodes().ToArray();
        nodesAfter.Length.Should().Be(beforeNodes.Length);
    }

    private static Task<IndexedRepoBuilder> CreateRepoAsync()
        => IndexedRepoBuilder.CreateAsync(options =>
        {
            options.MeterName = "RepoQL.Tests.Indexer";
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            ConfigureFormats(options);
        });

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
