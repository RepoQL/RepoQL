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
internal class MermaidXrayTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "mermaid").WithKind("mermaid.doc");
    }

    private sealed class Observer(Action onCompleted, Action<Exception> onError, Action<IndexerEvent> onNext)
        : IObserver<IndexerEvent>
    {
        public void OnCompleted() => onCompleted();
        public void OnError(Exception error) => onError(error);
        public void OnNext(IndexerEvent value) => onNext(value);
    }

    [Test]
    public async Task Mermaid_Indexer_Populates_Xray_Fields_On_Artifact()
    {
        // Arrange: simple flowchart with 4 nodes, 3 edges
        var fs = new MemoryFileSystem("repo");
        var mmd = "flowchart TD\nA[Start] --> B{Check}\nB -->|Yes| C[OK]\nB -->|No| D[Fail]\n";
        fs.AddOrUpdateText("diagrams/flow.mmd", mmd);
        var uri = RepoUri.Parse("mem://repo/diagrams/flow.mmd");

        using var store = new DuckDbGraphStore(":memory:", enableExtensions: false, registerUdfs: false);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var registry = new FormatRegistry(
        [
            new(
                    SemanticMediaType.Create("text","mermaid").WithKind("mermaid.doc"),
                    new Formats.Mermaid.MermaidLoader(),
                    new Formats.Mermaid.MermaidAnalyzer(),
                    new Formats.Mermaid.MermaidLoader(),
                    ["mermaid", "mmd"])
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

        // Assert x-ray fields present and coherent
        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        artifact.Structure.Should().NotBeNullOrWhiteSpace();

        var hl = artifact.Headline!.ToLowerInvariant();
        hl.Should().Contain("diagram:");
        hl.Should().Contain("nodes");
        hl.Should().Contain("edges");

        artifact.Summary!.ToLowerInvariant().Should().Contain("diagram:");
        artifact.Summary!.ToLowerInvariant().Should().Contain("flow:");

        artifact.Structure!.Should().Contain("Flowchart");
        artifact.Structure!.Should().Contain("- A: Start");

        await indexer.StopAsync(CancellationToken.None);
    }
}
