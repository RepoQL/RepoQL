using System.Diagnostics.Metrics;
using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Metrics;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.Formats.Markdown;
using RepoQL.Formats.Mermaid;

namespace RepoQL.Tests;

internal class IndexerIntegrationTests
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
    [Timeout(60_000)]
    public async Task StartAndWaitForIdle_IndexesMarkdownDocument_InMemoryDb(CancellationToken token)
    {
        // Arrange: embedded repo with markdown resources
        var asm = typeof(IndexerIntegrationTests).Assembly;

        using var meter = new Meter("RepoQL.Tests.Indexer");
        using var metrics = new IndexingMetrics();
        var vfs = new FileSystem.Embedded.EmbeddedStore(asm);
        var fsRegistry = new FileSystemRegistry([vfs]);
        var hub = new MultiFileSystem(fsRegistry, [vfs]);
        using var store = new DuckDbGraphStore(":memory:", metrics);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);
        var uri1 = RepoUri.Parse($"embed:///Resources/Doc1.md");
        var filter = new IncludeOnlyUriFilter(uri1);
        await using var indexer = new RepositoryIndexer(hub, store, classifier, formatRegistry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        // Act: start, explicitly queue the file, and wait for the pipeline to drain
        var errored = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = indexer.Subscribe(new Observer(
            onCompleted: () => { },
            onError: ex => errored.TrySetResult(ex),
            onNext: _ => { }));

        await indexer.StartAsync(token);
        await indexer.QueueForIndexingAsync([uri1]);
        await WaitForIdleOrErrorAsync(indexer, errored.Task, token, uri1);

        // Assert: document and some children exist
        var nodes = store.GetAllNodes().ToArray();
        nodes.Should().NotBeEmpty();
        nodes.Count(n => n.Kind == "document").Should().Be(1);
        nodes.Any(n => n.Kind == "md_heading").Should().BeTrue();
        nodes.Any(n => n.Kind == "md_link").Should().BeTrue();
        nodes.Any(n => n.Kind == "md_code_block").Should().BeTrue();

        // Cleanup
        await indexer.StopAsync(token);
    }

    [Test]
    public async Task WaitForIdle_ReflectsNewlyQueuedFiles()
    {
        // Arrange
        var asm = typeof(IndexerIntegrationTests).Assembly;
        var asmName = asm.GetName().Name ?? "RepoQL.Tests";

        using var meter = new Meter("RepoQL.Tests.Indexer");
        using var metrics = new IndexingMetrics();
        var vfs = new FileSystem.Embedded.EmbeddedStore(asm);
        var fsRegistry = new FileSystemRegistry([vfs]);
        var hub = new MultiFileSystem(fsRegistry, [vfs]);
        using var store = new DuckDbGraphStore(":memory:", metrics);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var (formatRegistry, workspace) = CreateFormats(hub, classifier, hasher);
        var uri1w = RepoUri.Parse($"embed:///Resources/Doc1.md");
        var uri2 = RepoUri.Parse($"embed:///Resources/Doc2.md");
        var filter = new IncludeOnlyUriFilter(uri1w);
        await using var indexer = new RepositoryIndexer(hub, store, classifier, formatRegistry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        var errored = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = indexer.Subscribe(new Observer(
            onCompleted: () => { },
            onError: ex => errored.TrySetResult(ex),
            onNext: _ => { }));

        await indexer.StartAsync(CancellationToken.None);
        await indexer.QueueForIndexingAsync([uri1w]);
        await WaitForIdleOrErrorAsync(indexer, errored.Task, CancellationToken.None, uri1w);

        // Assert initial doc count
        var before = store.GetAllNodes().Count(n => n.Kind == "document");
        before.Should().Be(1);

        // Act: add a new file and queue explicitly
        await indexer.QueueForIndexingAsync([uri2]);
        await WaitForIdleOrErrorAsync(indexer, errored.Task, CancellationToken.None, uri2);

        // Assert: document count increased
        var after = store.GetAllNodes().Count(n => n.Kind == "document");
        after.Should().Be(2);

        await indexer.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForIdleOrErrorAsync(RepositoryIndexer indexer, Task<Exception> errorTask, CancellationToken cancellationToken, RepoUri? uri)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idleTask = indexer.WaitForIdle(cancellationToken);
        var timeoutTask = Task.Delay(DefaultTimeout, timeoutCts.Token);

        var completed = await Task.WhenAny(idleTask, errorTask, timeoutTask);
        if (completed == errorTask)
            throw await errorTask;

        if (completed == timeoutTask)
        {
            if (timeoutTask.IsCanceled && cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            var message = uri is null
                ? "Timed out waiting for the indexer to become idle."
                : $"Timed out waiting to index {uri.AbsoluteUri}";
            throw new TimeoutException(message);
        }

        timeoutCts.Cancel();
        await idleTask;
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
