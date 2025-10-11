using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using RepoQL.Core.Analysis;
using RepoQL.Contracts;
using RepoQL.Core;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.Abstractions;
using RepoQL.FileSystem.InMemory;

namespace RepoQL.Tests;

[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope")]
internal class MarkdownXrayRichTests
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
    public async Task Markdown_Xray_Reports_Images_Tables_Frontmatter()
    {
        // Arrange: frontmatter, table, image
        var fs = new MemoryFileSystem("repo");
        var md = "---\nlayout: post\ntitle: Sample\ntags: [auth, oauth]\n---\n\n# Title\n\n![img](path.png)\n\n| a | b |\n|---|---|\n| 1 | 2 |\n";
        fs.AddOrUpdateText("docs/rich.md", md);
        var uri = RepoUri.Parse("mem://repo/docs/rich.md");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var registry = new FormatRegistry(
        [
            new(
                    SemanticMediaType.Create("text","markdown").WithKind("markdown.doc"),
                    new Formats.Markdown.MarkdownLoader(),
                    new Formats.Markdown.MarkdownAnalyzer(),
                    new Formats.Markdown.MarkdownLoader(),
                    ["markdown"])
        ]);
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

        // Assert
        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        artifact.Headline!.ToLowerInvariant().Should().Contain("images: 1");
        artifact.Headline!.ToLowerInvariant().Should().Contain("tables: 1");
        artifact.Summary!.ToLowerInvariant().Should().Contain("frontmatter: 3");
        var hl = artifact.Headline!.ToLowerInvariant();
        hl.Should().Contain("#auth");
        hl.Should().Contain("#oauth");

        await indexer.StopAsync(CancellationToken.None);
    }
}
