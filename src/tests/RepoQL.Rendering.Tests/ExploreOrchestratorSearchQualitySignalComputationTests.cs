using AwesomeAssertions;
using FakeItEasy;
using RepoQL.Explore;
using RepoQL.Explore.Search;

namespace RepoQL.Rendering.Tests;

public class ExploreOrchestratorSearchQualitySignalComputationTests
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
    public async Task Given_InventoryWithoutKeywords_Then_QualityTierIsExhaustive()
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

        var output = await ExecuteAsync(searchResult, keywords: null, intent: Intent.Inventory);

        output.Should().Contain("quality: exhaustive");
    }

    [Test]
    public async Task Given_TopResultAtStrongBoundary_Then_QualityTierIsModerate()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/boundary.cs", rawScore: 0.70)
            ],
            TotalDocumentsMatched: 1,
            TotalObjectsMatched: 0,
            TrustSignal: null);

        var output = await ExecuteAsync(searchResult, keywords: "boundary");

        output.Should().Contain("quality: moderate");
        output.Should().NotContain("quality: strong");
    }

    [Test]
    public async Task Given_FirstResultIsNotHighestScore_Then_QualityUsesTopResultOnly()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/top.cs", rawScore: 0.45),
                CreateDocumentResult("file:///src/later.cs", rawScore: 0.95)
            ],
            TotalDocumentsMatched: 2,
            TotalObjectsMatched: 0,
            TrustSignal: null);

        var output = await ExecuteAsync(searchResult, keywords: "auth");

        output.Should().Contain("quality: moderate");
        output.Should().NotContain("quality: strong");
    }

    [Test]
    public async Task Given_TopResultZeroScore_Then_QualityTierIsOmitted()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/zero.cs", rawScore: 0.0)
            ],
            TotalDocumentsMatched: 1,
            TotalObjectsMatched: 0,
            TrustSignal: null);

        var output = await ExecuteAsync(searchResult, keywords: "zero");

        output.Should().NotContain("quality:");
    }

    [Test]
    public async Task Given_WideScopeKeywordSearch_Then_CoverageCountsScoresAboveThreshold()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/one.cs", rawScore: 0.91),
                CreateDocumentResult("file:///src/two.cs", rawScore: 0.41),
                CreateDocumentResult("file:///src/three.cs", rawScore: 0.40),
                CreateDocumentResult("file:///src/four.cs", rawScore: 0.15)
            ],
            TotalDocumentsMatched: 80,
            TotalObjectsMatched: 0,
            TrustSignal: null);

        var output = await ExecuteAsync(searchResult, keywords: "auth");

        output.Should().Contain("2 of 80 above threshold");
        output.Should().NotContain("all in scope");
    }

    [Test]
    public async Task Given_AllScoredDocumentsAboveThreshold_Then_CoverageShowsAllInScope()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/one.cs", rawScore: 0.95),
                CreateDocumentResult("file:///src/two.cs", rawScore: 0.89),
                CreateDocumentResult("file:///src/three.cs", rawScore: 0.50)
            ],
            TotalDocumentsMatched: 40,
            TotalObjectsMatched: 0,
            TrustSignal: null);

        var output = await ExecuteAsync(searchResult, keywords: "auth");

        output.Should().Contain("3 matches (all in scope)");
    }

    [Test]
    public async Task Given_SearchResultWithProvenance_Then_ExploreResultCarriesProvenance()
    {
        var searchResult = new SearchEngineResult(
            Results:
            [
                CreateDocumentResult("file:///src/auth.cs", rawScore: 0.91) with
                {
                    Provenance = "semantic"
                }
            ],
            TotalDocumentsMatched: 1,
            TotalObjectsMatched: 0,
            TrustSignal: null);

        var searchEngine = A.Fake<IExploreSearchEngine>();
        A.CallTo(() => searchEngine.SearchAsync(
                A<SearchParameters>._,
                A<IJitObjectSearchService?>._,
                A<JitEmbeddingCache?>._,
                A<CancellationToken>._))
            .Returns(searchResult);

        var orchestrator = new ExploreOrchestrator(searchEngine);
        var execution = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 1200,
                Intent: Intent.Locate,
                Scope: null,
                Keywords: "auth",
                Boost: null,
                Penalize: null,
                Limit: null),
            DefaultStatus,
            CancellationToken.None);

        execution.Results.Should().HaveCount(1);
        execution.Results[0].Provenance.Should().Be("semantic");
    }

    private static async Task<string> ExecuteAsync(
        SearchEngineResult searchResult,
        string? keywords,
        Intent intent = Intent.Locate)
    {
        var searchEngine = A.Fake<IExploreSearchEngine>();
        A.CallTo(() => searchEngine.SearchAsync(
                A<SearchParameters>._,
                A<IJitObjectSearchService?>._,
                A<JitEmbeddingCache?>._,
                A<CancellationToken>._))
            .Returns(searchResult);

        var orchestrator = new ExploreOrchestrator(searchEngine);
        var query = new ExploreQuery(
            TokenBudget: 1200,
            Intent: intent,
            Scope: null,
            Keywords: keywords,
            Boost: null,
            Penalize: null,
            Limit: null);

        var execution = await orchestrator.ExecuteAsync(query, DefaultStatus, CancellationToken.None);
        return execution.RenderedOutput;
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
}
