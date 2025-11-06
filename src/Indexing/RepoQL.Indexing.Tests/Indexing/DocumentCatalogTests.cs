using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.State;

namespace RepoQL.Indexing.Tests.Indexing;

public class DocumentCatalogTests
{
    [Test]
    [DisplayName("Hydrates entries only once even when initialization is invoked repeatedly")]
    public async Task EnsureInitializedAsync_LoadsEntriesOnce()
    {
        // Arrange
        var uri = ParseUri("file:///repo/README.md");
        var entry = new DocumentCatalogEntry(
            uri,
            "ABC123",
            SemanticMediaType.Parse("text/markdown;kind=markdown.doc"),
            "C:\\repo\\README.md",
            DateTimeOffset.UtcNow);

        var dataSource = new RecordingDataSource([entry]);
        var catalog = new DocumentCatalog(dataSource);

        // Act
        await Task.WhenAll(
            catalog.EnsureInitializedAsync(CancellationToken.None),
            catalog.EnsureInitializedAsync(CancellationToken.None));

        await catalog.EnsureInitializedAsync(CancellationToken.None);
        var evaluation = catalog.Evaluate(uri, "ABC123");

        // Assert
        dataSource.LoadCallCount.Should().Be(1);
        evaluation.Decision.Should().Be(DocumentCatalogDecision.SkipUpToDate);
        evaluation.Existing.Should().Be(entry);
    }

    [Test]
    [DisplayName("Evaluate returns Reindex when digest differs from catalog entry")]
    public async Task Evaluate_ReturnsReindex_WhenDigestDiffers()
    {
        // Arrange
        var uri = ParseUri("file:///repo/file.txt");
        var entry = new DocumentCatalogEntry(
            uri,
            "OLD",
            SemanticMediaType.Parse("text/plain"),
            null,
            DateTimeOffset.UtcNow.AddDays(-1));

        var catalog = new DocumentCatalog(new RecordingDataSource([entry]));
        await catalog.EnsureInitializedAsync(CancellationToken.None);

        // Act
        var evaluation = catalog.Evaluate(uri, "NEW");

        // Assert
        evaluation.Decision.Should().Be(DocumentCatalogDecision.Reindex);
        evaluation.Existing.Should().Be(entry);
    }

    [Test]
    [DisplayName("Pending digests short-circuit duplicate work until processing completes")]
    public async Task Evaluate_SkipsWhilePendingDigestMatches()
    {
        // Arrange
        var uri = ParseUri("file:///repo/pending.cs");
        var catalog = new DocumentCatalog(new RecordingDataSource(Array.Empty<DocumentCatalogEntry>()));
        await catalog.EnsureInitializedAsync(CancellationToken.None);

        const string digest = "DEADBEEF";

        // Act
        catalog.BeginProcessing(uri, digest);
        var pendingEvaluation = catalog.Evaluate(uri, digest);
        catalog.CompleteProcessing(uri);
        var finalEvaluation = catalog.Evaluate(uri, digest);

        // Assert
        pendingEvaluation.Decision.Should().Be(DocumentCatalogDecision.SkipUpToDate);
        finalEvaluation.Decision.Should().Be(DocumentCatalogDecision.Unknown);
    }

    private static RepoUri ParseUri(string value)
    {
        return RepoUri.TryParse(value, out var parsed)
            ? parsed!
            : throw new InvalidOperationException($"Failed to parse URI '{value}'.");
    }

    private sealed class RecordingDataSource(IReadOnlyList<DocumentCatalogEntry> entries) : IDocumentCatalogDataSource
    {
        private int _loadCallCount;
        public int LoadCallCount => _loadCallCount;

        public Task<IReadOnlyList<DocumentCatalogEntry>> LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loadCallCount);
            return Task.FromResult(entries);
        }
    }
}
