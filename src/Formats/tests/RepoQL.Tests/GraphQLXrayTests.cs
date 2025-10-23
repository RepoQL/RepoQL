using AwesomeAssertions;
using RepoQL.Core;
using RepoQL.Core.Analysis;
using RepoQL.Data.DuckDB;
using RepoQL.FileSystem;
using RepoQL.FileSystem.InMemory;
using RepoQL.Formats.GraphQL;
using RepoQL.Contracts;

namespace RepoQL.Tests;

public class GraphQLXrayTests
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private sealed class StubClassifier : IFileClassifier
    {
        public SemanticMediaType GetMediaType(Microsoft.Extensions.FileProviders.IFileInfo fileInfo)
            => SemanticMediaType.Create("text", "graphql").WithKind("graphql.doc");
    }

    private sealed class Observer(Action onCompleted, Action<Exception> onError, Action<IndexerEvent> onNext)
        : IObserver<IndexerEvent>
    {
        public void OnCompleted() => onCompleted();
        public void OnError(Exception error) => onError(error);
        public void OnNext(IndexerEvent value) => onNext(value);
    }

    [Test]
    public async Task GraphQL_Indexer_Populates_Xray()
    {
        var fs = new MemoryFileSystem("repo");
        var gql = """
            query Hero($id: ID!) {
              hero(id: $id) {
                name
                friends {
                  name
                }
              }
            }

            fragment FriendFields on Character {
              name
            }
            """;
        fs.AddOrUpdateText("api/hero.graphql", gql);
        var uri = RepoUri.Parse("mem://repo/api/hero.graphql");

        using var store = new DuckDbGraphStore(":memory:", new RepoQL.Metrics.IndexingMetrics(), enableExtensions: false, registerUdfs: true);
        var classifier = new StubClassifier();
        var hasher = new XxHasher();
        var filter = new NoOpUriFilter();
        var fsRegistry = new FileSystemRegistry([fs]);
        var hub = new MultiFileSystem(fsRegistry, [fs]);
        var graphQlLoader = new GraphQLLoader();
        var graphQlAnalyzer = new GraphQLAnalyzer();
        var plainLoader = new PlainTextLoader();
        var plainAnalyzer = new NullAnalyzer(SemanticMediaType.Create("text", "plain").WithKind("plain.document"));

        var registry = new FormatRegistry(
            new[]
            {
                new FormatDescriptor(
                    SemanticMediaType.Create("text","graphql").WithKind("graphql.doc"),
                    graphQlLoader,
                    graphQlAnalyzer,
                    graphQlLoader,
                    ["graphql", "gql"]),
                new FormatDescriptor(
                    SemanticMediaType.Create("text","plain").WithKind("plain.document"),
                    plainLoader,
                    plainAnalyzer,
                    plainLoader)
            });

        var workspace = new AnalysisWorkspace(hub, classifier, hasher, registry);
        await using var indexer = new RepositoryIndexer(new Core.Metrics.IndexingMetrics(), new System.Diagnostics.Metrics.Meter("RepoQL.Tests.GraphQL"), hub, store, classifier, registry, workspace, filter, hasher, analysisWriter: new AnnotationResultWriter(store));

        await indexer.StartAsync(CancellationToken.None);

        var indexed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = indexer.Subscribe(new Observer(() => { }, _ => { }, ev =>
        {
            if (ev is Core.IRepositoryIndexer.ItemIndexedEvent e && e.CurrentUri.AbsoluteUri == uri.AbsoluteUri)
                indexed.TrySetResult(true);
        }));

        var done = await Task.WhenAny(indexed.Task, Task.Delay(DefaultTimeout));
        if (done != indexed.Task) throw new TimeoutException("Timed out waiting for index");

        var doc = store.GetDocumentByUri(uri)!;
        var artifact = store.GetArtifact(doc.ArtifactId!.Value)!;

        artifact.Headline.Should().NotBeNullOrWhiteSpace();
        artifact.Headline!.ToLowerInvariant().Should().Contain("graphql");

        artifact.Summary.Should().NotBeNullOrWhiteSpace();
        var summary = artifact.Summary!.ToLowerInvariant();
        summary.Should().Contain("operations:");
        summary.Should().Contain("fragments:");

        artifact.Structure.Should().NotBeNullOrWhiteSpace();
        var structure = artifact.Structure!.ToLowerInvariant();
        structure.Should().Contain("operations");
        structure.Should().Contain("fragment friendfields");
        structure.Should().Contain("hero");

        await indexer.StopAsync(CancellationToken.None);
    }
}
