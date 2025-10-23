using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;

namespace RepoQL.Tests;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope")]
internal class ReindexingMemoryFsTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
        {
            var name = fileInfo.Name.ToLowerInvariant();
            if (name.EndsWith(".md") || name.EndsWith(".markdown"))
                return SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
            return SemanticMediaType.Create("text", "plain");
        }
    }

    private sealed class Observer(Action onCompleted, Action<Exception> onError, Action<IndexerEvent> onNext)
        : IObserver<IndexerEvent>
    {
        public void OnCompleted() => onCompleted();
        public void OnError(Exception error) => onError(error);
        public void OnNext(IndexerEvent value) => onNext(value);
    }

    [Test]
    public async Task Reindex_UpdatesExistingDocument_AndReplacesSubtree()
    {
        // Arrange in-memory FS with initial content
        var fs = new MemoryFileSystem("repo");
        fs.AddOrUpdateText("docs/x.md", "# One\n\n## Two\n\n```txt\ncode\n```\n");
        var uri = RepoUri.Parse("mem://repo/docs/x.md");

        var meter = new Meter("RepoQL.Tests.Indexer");
        var metrics = new IndexingMetrics();
        using var store = new DuckDbGraphStore(":memory:", new RepoQL.Metrics.IndexingMetrics());
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);
        await using var indexer = new RepositoryIndexer(metrics, meter, hub, store, classifier, formatRegistry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);

        // Wait for first index
        var firstIndexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub1 = indexer.Subscribe(new Observer(() => { }, ex => firstError.TrySetResult(ex), ev =>
        {
            if (ev is IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri.AbsoluteUri)
                firstIndexed.TrySetResult(true);
        }));
        var done1 = await Task.WhenAny(firstIndexed.Task, firstError.Task, Task.Delay(DefaultTimeout));
        if (done1 == firstError.Task) throw await firstError.Task;
        if (done1 != firstIndexed.Task) throw new TimeoutException("Timed out waiting for initial index");

        // Assert initial state
        var doc = store.GetDocumentByUri(uri);
        doc.Should().NotBeNull();
        var nodesBefore = store.GetAllNodes().ToArray();
        nodesBefore.Count(n => n.Kind == "document").Should().Be(1);
        nodesBefore.Count(n => n.Kind == "md_heading").Should().Be(2);

        // Mutate file content and wait for reindex
        fs.AddOrUpdateText("docs/x.md", "# Only\n\nText\n");

        var secondIndexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub2 = indexer.Subscribe(new Observer(() => { }, ex => secondError.TrySetResult(ex), ev =>
        {
            if (ev is IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri.AbsoluteUri)
                secondIndexed.TrySetResult(true);
        }));
        var done2 = await Task.WhenAny(secondIndexed.Task, secondError.Task, Task.Delay(DefaultTimeout));
        if (done2 == secondError.Task) throw await secondError.Task;
        if (done2 != secondIndexed.Task) throw new TimeoutException("Timed out waiting for reindex");

        // Assert updated state
        var nodesAfter = store.GetAllNodes().ToArray();
        nodesAfter.Count(n => n.Kind == "document").Should().Be(1);
        nodesAfter.Count(n => n.Kind == "md_heading").Should().Be(1);

        await indexer.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task Reindex_Unchanged_ShortCircuits_LeavesDocumentUntouched()
    {
        var fs = new MemoryFileSystem("repo");
        var content = "# A\n\nText\n";
        fs.AddOrUpdateText("docs/y.md", content);
        var uri = RepoUri.Parse("mem://repo/docs/y.md");

        var meter = new Meter("RepoQL.Tests.Indexer");
        var metrics = new IndexingMetrics();
        using var store = new DuckDbGraphStore(":memory:", new RepoQL.Metrics.IndexingMetrics());
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);
        await using var indexer = new RepositoryIndexer(metrics, meter, hub, store, classifier, formatRegistry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);

        var firstIndexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub1 = indexer.Subscribe(new Observer(() => { }, _ => { }, ev =>
        {
            if (ev is IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri.AbsoluteUri)
                firstIndexed.TrySetResult(true);
        }));
        await Task.WhenAny(firstIndexed.Task, Task.Delay(DefaultTimeout));

        var before = store.GetDocumentByUri(uri)!;
        var beforeUpdated = before.UpdatedAt;
        var beforeArtifact = before.ArtifactId;
        var nodesBefore = store.GetAllNodes().ToArray();

        // Re-add same content (should emit ItemIndexed via short-circuit, without DB modifications)
        var secondIndexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub2 = indexer.Subscribe(new Observer(() => { }, _ => { }, ev =>
        {
            if (ev is IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri.AbsoluteUri)
                secondIndexed.TrySetResult(true);
        }));
        fs.AddOrUpdateText("docs/y.md", content);
        await Task.WhenAny(secondIndexed.Task, Task.Delay(DefaultTimeout));

        var after = store.GetDocumentByUri(uri)!;
        var nodesAfter = store.GetAllNodes().ToArray();

        // Assert document unchanged and counts same
        after.UpdatedAt.Should().Be(beforeUpdated);
        after.ArtifactId.Should().Be(beforeArtifact);
        nodesAfter.Length.Should().Be(nodesBefore.Length);

        await indexer.StopAsync(CancellationToken.None);
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
            new FormatDescriptor(
                SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc"),
                markdownLoader,
                markdownAnalyzer,
                markdownLoader,
                ["markdown"]),
            new FormatDescriptor(
                SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"),
                mermaidLoader,
                mermaidAnalyzer,
                mermaidLoader,
                ["mermaid", "mmd"]),
            new FormatDescriptor(
                SemanticMediaType.Create("text", "plain").WithKind("plain.document"),
                plainLoader,
                plainAnalyzer,
                plainLoader)
        };

        var registry = new FormatRegistry(descriptors);
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        return (registry, workspace);
    }
}
