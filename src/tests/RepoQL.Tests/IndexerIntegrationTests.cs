using System.Diagnostics.Metrics;
using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Core.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;

namespace RepoQL.Tests;

public class IndexerIntegrationTests
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

    // Test-only filter that includes only a fixed set of URIs.
    private sealed class IncludeOnlyUriFilter : IUriFilter
    {
        private readonly HashSet<string> _allow;
        public IncludeOnlyUriFilter(params RepoUri[] allowed)
        {
            _allow = new HashSet<string>(allowed.Select(u => u.AbsoluteUri.ToLowerInvariant()));
        }
        public bool IncludeFile(RepoUri uri) => _allow.Contains(uri.AbsoluteUri.ToLowerInvariant());
    }

    [Test]
    public async Task StartAndWaitForIdle_IndexesMarkdownDocument_InMemoryDb()
    {
        // Arrange: embedded repo with markdown resources
        var asm = typeof(IndexerIntegrationTests).Assembly;
        var asmName = asm.GetName().Name ?? "RepoQL.Tests";

        var meter = new Meter("RepoQL.Tests.Indexer");
        var metrics = new IndexingMetrics();
        var vfs = new FileSystem.Embedded.EmbeddedStore(asm);
        var fsRegistry = new FileSystemRegistry([vfs]);
        var hub = new MultiFileSystem(fsRegistry, [vfs]);
        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);
        var uri1 = RepoUri.Parse($"embed:///Resources/Doc1.md");
        var filter = new IncludeOnlyUriFilter(uri1);
        await using var indexer = new RepositoryIndexer(metrics, meter, hub, store, classifier, formatRegistry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        // Act: start, then explicitly queue the file to avoid any enumeration/platform quirks
        await indexer.StartAsync(CancellationToken.None);
        var indexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errored = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = indexer.Subscribe(new Observer(
            onCompleted: () => { },
            onError: ex => errored.TrySetResult(ex),
            onNext: ev =>
            {
                if (ev is IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri1.AbsoluteUri)
                    indexed.TrySetResult(true);
            }));
        var done = await Task.WhenAny(indexed.Task, errored.Task, Task.Delay(DefaultTimeout));
        if (done == errored.Task)
            throw await errored.Task;
        else if (done != indexed.Task)
            throw new TimeoutException($"Timed out waiting to index {uri1.AbsoluteUri}");

        // Assert: document and some children exist
        var nodes = store.GetAllNodes().ToArray();
        nodes.Should().NotBeEmpty();
        nodes.Count(n => n.Kind == "document").Should().Be(1);
        nodes.Any(n => n.Kind == "md_heading").Should().BeTrue();
        nodes.Any(n => n.Kind == "md_link").Should().BeTrue();
        nodes.Any(n => n.Kind == "md_code_block").Should().BeTrue();

        // Cleanup
        await indexer.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task WaitForIdle_ReflectsNewlyQueuedFiles()
    {
        // Arrange
        var asm = typeof(IndexerIntegrationTests).Assembly;
        var asmName = asm.GetName().Name ?? "RepoQL.Tests";

        var meter = new Meter("RepoQL.Tests.Indexer");
        var metrics = new IndexingMetrics();
        var vfs = new FileSystem.Embedded.EmbeddedStore(asm);
        var fsRegistry = new FileSystemRegistry([vfs]);
        var hub = new MultiFileSystem(fsRegistry, [vfs]);
        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);
        var uri1w = RepoUri.Parse($"embed:///Resources/Doc1.md");
        var uri2 = RepoUri.Parse($"embed:///Resources/Doc2.md");
        var filter = new IncludeOnlyUriFilter(uri1w);
        await using var indexer = new RepositoryIndexer(metrics, meter, hub, store, classifier, formatRegistry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);
        var firstIndexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error1 = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub1 = indexer.Subscribe(new Observer(
            onCompleted: () => { },
            onError: ex => error1.TrySetResult(ex),
            onNext: ev =>
            {
                if (ev is IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri1w.AbsoluteUri)
                    firstIndexed.TrySetResult(true);
            }));
        var firstDone = await Task.WhenAny(firstIndexed.Task, error1.Task, Task.Delay(DefaultTimeout));
        if (firstDone == error1.Task)
            throw await error1.Task;
        else if (firstDone != firstIndexed.Task)
            throw new TimeoutException($"Timed out waiting to index {uri1w.AbsoluteUri}");

        // Assert initial doc count
        var before = store.GetAllNodes().Count(n => n.Kind == "document");
        before.Should().Be(1);

        // Act: add a new file and queue explicitly
        var secondIndexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error2 = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub2 = indexer.Subscribe(new Observer(
            onCompleted: () => { },
            onError: ex => error2.TrySetResult(ex),
            onNext: ev =>
            {
                if (ev is IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri2.AbsoluteUri)
                    secondIndexed.TrySetResult(true);
            }));
        await indexer.QueueForIndexingAsync(uri2);

        var secondDone = await Task.WhenAny(secondIndexed.Task, error2.Task, Task.Delay(DefaultTimeout));
        if (secondDone == error2.Task)
            throw await error2.Task;
        else if (secondDone != secondIndexed.Task)
            throw new TimeoutException($"Timed out waiting to index {uri2.AbsoluteUri}");

        // Assert: document count increased
        var after = store.GetAllNodes().Count(n => n.Kind == "document");
        after.Should().Be(2);

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
                new[] { "markdown" }),
            new FormatDescriptor(
                SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc"),
                mermaidLoader,
                mermaidAnalyzer,
                mermaidLoader,
                new[] { "mermaid", "mmd" }),
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
