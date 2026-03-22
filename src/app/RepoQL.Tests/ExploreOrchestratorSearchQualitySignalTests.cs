using AwesomeAssertions;
using RepoQL.Explore;
using RepoQL.Explore.Search;

namespace RepoQL.Tests;

internal sealed class ExploreOrchestratorSearchQualitySignalTests
{
    private static readonly TrustSignal DefaultStatus = new(
        IndexTotal: 100,
        IndexPending: 0,
        IndexFailed: 0,
        IndexStale: 0,
        SemanticEnabled: true,
        SemanticReady: true,
        SemanticPercent: 100,
        ExecutionTimeMs: 25);

    [Test]
    public async Task InventoryWithoutKeywords_ShowsExhaustiveQuality()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/a.cs", rawScore: 0.20),
                CreateDocumentResult("file:///src/b.cs", rawScore: 0.10)
            ],
            TotalDocumentsMatched: 2,
            TotalObjectsMatched: 0,
            TrustSignal: null);
        var orchestrator = new ExploreOrchestrator(new StubSearchEngine(searchResult));

        var output = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 1000,
                Breadth: 8,
                Scope: null,
                Keywords: null,
                Boost: null,
                Penalize: null,
                Limit: null),
            DefaultStatus,
            CancellationToken.None);

        output.RenderedOutput.Should().Contain("quality: exhaustive");
        output.RenderedOutput.Should().NotContain("above threshold");
    }

    [Test]
    public async Task KeywordsWithWideScope_ShowQualityAndCoverage()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/auth.cs", rawScore: 0.91),
                CreateDocumentResult("file:///src/log.cs", rawScore: 0.35),
                CreateDocumentResult("file:///src/cache.cs", rawScore: 0.20)
            ],
            TotalDocumentsMatched: 30,
            TotalObjectsMatched: 0,
            TrustSignal: null);
        var orchestrator = new ExploreOrchestrator(new StubSearchEngine(searchResult));

        var output = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 1200,
                Breadth: 5,
                Scope: null,
                Keywords: "authentication",
                Boost: null,
                Penalize: null,
                Limit: null),
            DefaultStatus,
            CancellationToken.None);

        output.RenderedOutput.Should().Contain("quality: strong");
        output.RenderedOutput.Should().Contain("1 of 30 above threshold");
    }

    [Test]
    public async Task KeywordsWithNarrowScope_OmitsCoverageSignal()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/a.cs", rawScore: 0.55),
                CreateDocumentResult("file:///src/b.cs", rawScore: 0.42)
            ],
            TotalDocumentsMatched: 10,
            TotalObjectsMatched: 0,
            TrustSignal: null);
        var orchestrator = new ExploreOrchestrator(new StubSearchEngine(searchResult));

        var output = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 1200,
                Breadth: 5,
                Scope: null,
                Keywords: "search",
                Boost: null,
                Penalize: null,
                Limit: null),
            DefaultStatus,
            CancellationToken.None);

        output.RenderedOutput.Should().Contain("quality: moderate");
        output.RenderedOutput.Should().NotContain("above threshold");
        output.RenderedOutput.Should().NotContain("all in scope");
    }

    [Test]
    public async Task WideScopeAllDocumentsAboveThreshold_ShowsAllInScopeCoverage()
    {
        var results = Enumerable.Range(1, 20)
            .Select(i => CreateDocumentResult($"file:///src/doc{i}.cs", rawScore: 0.65))
            .ToArray();
        var searchResult = new SearchEngineResult(
            Results: results,
            TotalDocumentsMatched: 20,
            TotalObjectsMatched: 0,
            TrustSignal: null);
        var orchestrator = new ExploreOrchestrator(new StubSearchEngine(searchResult));

        var output = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 1200,
                Breadth: 5,
                Scope: null,
                Keywords: "service",
                Boost: null,
                Penalize: null,
                Limit: null),
            DefaultStatus,
            CancellationToken.None);

        output.RenderedOutput.Should().Contain("20 matches (all in scope)");
    }

    private static SearchResult CreateDocumentResult(string uri, double rawScore)
    {
        return new SearchResult(
            Uri: uri,
            Scope: SearchScope.Document,
            Kind: null,
            Symbol: null,
            Headline: $"Doc {uri}",
            Structure: null,
            Snippet: null,
            LineStart: null,
            LineEnd: null,
            Lang: "csharp",
            SemanticType: "code.csharp",
            RawScore: rawScore,
            Confidence: 50);
    }

    private sealed class StubSearchEngine(SearchEngineResult result) : IExploreSearchEngine
    {
        public Task<SearchEngineResult> SearchAsync(
            SearchParameters parameters,
            IJitObjectSearchService? jitService,
            JitEmbeddingCache? jitCache,
            CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}
