using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.InMemory;

namespace RepoQL.Tests;

public class MarkdownXrayTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");
    }

    private sealed class Observer(Action onCompleted, Action<Exception> onError, Action<IndexerEvent> onNext)
        : IObserver<IndexerEvent>
    {
        public void OnCompleted() => onCompleted();
        public void OnError(Exception error) => onError(error);
        public void OnNext(IndexerEvent value) => onNext(value);
    }

    [Test]
    public async Task Markdown_Indexer_Populates_Xray_Fields_On_Artifact()
    {
        // Arrange
        var fs = new MemoryFileSystem("repo");
        var md = "# Title\n\n## Section A\nText.\n\n```csharp\n// code\n```\n\n[link](#section-a)\n";
        fs.AddOrUpdateText("docs/readme.md", md);
        var uri = RepoUri.Parse("mem://repo/docs/readme.md");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var registry = new FormatRegistry(
            new Contracts.FormatDescriptor[]
            {
                // Use real Markdown loader/analyzer from RepoQL.Formats.Markdown
                new(
                    SemanticMediaType.Create("text","markdown").WithKind("markdown.doc"),
                    new Formats.Markdown.MarkdownLoader(),
                    new Formats.Markdown.MarkdownAnalyzer(),
                    new Formats.Markdown.MarkdownLoader(),
                    ["markdown"])
            });
        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new Core.RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.Xray"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);

        var indexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = indexer.Subscribe(new Observer(() => { }, _ => { }, ev =>
        {
            if (ev is Core.IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri.AbsoluteUri)
                indexed.TrySetResult(true);
        }));
        var done = await Task.WhenAny(indexed.Task, Task.Delay(DefaultTimeout));
        if (done != indexed.Task) throw new TimeoutException("Timed out waiting for index");

        // Act
        var doc = store.GetDocumentByUri(uri)!;
        var artifact = store.GetArtifact(doc.ArtifactId!.Value)!;

        // Assert x-ray fields present and coherent
        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        var hl = artifact.Headline!.ToLowerInvariant();
        hl.Should().Contain("markdown");
        hl.Should().Contain("lines");
        // New headline focuses on semantics: title/topics/lang instead of raw counts
        (hl.Contains("topics:") || hl.Contains("lang:")).Should().BeTrue();
        artifact.Summary!.ToLowerInvariant().Should().Contain("sections");
        artifact.Structure!.Should().Contain("- Title");
        artifact.Structure!.Should().Contain("- Section A");

        await indexer.StopAsync(CancellationToken.None);
    }
}
