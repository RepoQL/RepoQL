using AwesomeAssertions;
using RepoQL.Contracts.Embeddings;
using RepoQL.Explore.Search;

namespace RepoQL.Rendering.Tests;

public class ExploreSearchEngineRerankTests
{
    [Test]
    [DisplayName("Reranker changes document order")]
    public async Task Given_RerankReversesRelevance_Then_OrderChanges()
    {
        // 5 documents with descending scores
        var candidates = CreateDocumentCandidates(5);

        // Reranker reverses relevance: doc4 most relevant, doc0 least
        var reranker = new StubRerankProvider(reverseOrder: true);
        var searchEngine = new ExploreSearchEngine(
            new StubExploreCandidateService(new ExploreCandidateResult(candidates, TotalMatched: 5)),
            reranker);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(Scope: null, Question: "test query", Patterns: [], Breadth: 5, TokenBudget: 2000),
            jitService: null, jitCache: null, cancellationToken: CancellationToken.None);

        // doc0 was originally first but got demoted (moved down 4 positions)
        // It should no longer be first after reranking
        result.Results.Should().HaveCountGreaterThan(0);
        result.Results[0].Uri.Should().NotContain("doc0");
        // doc0 should be last (biggest demotion from highest base)
        result.Results[^1].Uri.Should().Contain("doc0");
    }

    [Test]
    [DisplayName("Reranker demotes document that moved down")]
    public async Task Given_RerankMovesDocDown_Then_ScoreDecreases()
    {
        var candidates = CreateDocumentCandidates(5);

        // Reranker reverses: doc0 (originally best) moves to last
        var reranker = new StubRerankProvider(reverseOrder: true);
        var searchEngine = new ExploreSearchEngine(
            new StubExploreCandidateService(new ExploreCandidateResult(candidates, TotalMatched: 5)),
            reranker);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(Scope: null, Question: "test query", Patterns: [], Breadth: 5, TokenBudget: 2000),
            jitService: null, jitCache: null, cancellationToken: CancellationToken.None);

        // doc0 should no longer be first
        result.Results[0].Uri.Should().NotContain("doc0");
    }

    [Test]
    [DisplayName("Reranker skipped when no query")]
    public async Task Given_NoQuery_Then_RerankSkipped()
    {
        var candidates = CreateDocumentCandidates(5);
        var reranker = new StubRerankProvider(reverseOrder: true);
        var searchEngine = new ExploreSearchEngine(
            new StubExploreCandidateService(new ExploreCandidateResult(candidates, TotalMatched: 5)),
            reranker);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(Scope: null, Question: null, Patterns: [], Breadth: 5, TokenBudget: 2000),
            jitService: null, jitCache: null, cancellationToken: CancellationToken.None);

        reranker.CallCount.Should().Be(0);
        // Original order preserved: doc0 first
        result.Results[0].Uri.Should().Contain("doc0");
    }

    [Test]
    [DisplayName("Reranker skipped when fewer than 5 documents")]
    public async Task Given_FewDocuments_Then_RerankSkipped()
    {
        var candidates = CreateDocumentCandidates(3);
        var reranker = new StubRerankProvider(reverseOrder: true);
        var searchEngine = new ExploreSearchEngine(
            new StubExploreCandidateService(new ExploreCandidateResult(candidates, TotalMatched: 3)),
            reranker);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(Scope: null, Question: "test", Patterns: [], Breadth: 5, TokenBudget: 2000),
            jitService: null, jitCache: null, cancellationToken: CancellationToken.None);

        reranker.CallCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Reranker skipped at high breadth")]
    public async Task Given_HighBreadth_Then_RerankSkipped()
    {
        var candidates = CreateDocumentCandidates(10);
        var reranker = new StubRerankProvider(reverseOrder: true);
        var searchEngine = new ExploreSearchEngine(
            new StubExploreCandidateService(new ExploreCandidateResult(candidates, TotalMatched: 10)),
            reranker);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(Scope: null, Question: "test", Patterns: [], Breadth: 8, TokenBudget: 2000),
            jitService: null, jitCache: null, cancellationToken: CancellationToken.None);

        reranker.CallCount.Should().Be(0);
    }

    [Test]
    [DisplayName("Reranker failure is gracefully handled")]
    public async Task Given_RerankFails_Then_OriginalOrderPreserved()
    {
        var candidates = CreateDocumentCandidates(5);
        var reranker = new FailingRerankProvider();
        var searchEngine = new ExploreSearchEngine(
            new StubExploreCandidateService(new ExploreCandidateResult(candidates, TotalMatched: 5)),
            reranker);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(Scope: null, Question: "test", Patterns: [], Breadth: 5, TokenBudget: 2000),
            jitService: null, jitCache: null, cancellationToken: CancellationToken.None);

        // Original order preserved despite failure
        result.Results[0].Uri.Should().Contain("doc0");
    }

    [Test]
    [DisplayName("Relevance modifier has a floor")]
    public async Task Given_LowRelevance_Then_ScoreFlooredAboveZero()
    {
        // Create many docs so lowest-relevance doc gets a strong penalty
        var candidates = CreateDocumentCandidates(20);

        // Reranker reverses: doc0 gets lowest relevance → strong penalty, but floor prevents zero
        var reranker = new StubRerankProvider(reverseOrder: true);
        var searchEngine = new ExploreSearchEngine(
            new StubExploreCandidateService(new ExploreCandidateResult(candidates, TotalMatched: 20)),
            reranker);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(Scope: null, Question: "test", Patterns: [], Breadth: 5, TokenBudget: 2000),
            jitService: null, jitCache: null, cancellationToken: CancellationToken.None);

        // All scores should be >= 0
        result.Results.Should().AllSatisfy(r => r.RawScore.Should().BeGreaterThanOrEqualTo(0));
    }

    private static List<ExploreCandidate> CreateDocumentCandidates(int count)
    {
        var candidates = new List<ExploreCandidate>();
        for (var i = 0; i < count; i++)
        {
            var docId = Guid.NewGuid();
            var score = 1.0 - i * 0.05; // Descending scores
            candidates.Add(new ExploreCandidate(
                DocId: docId, NodeId: Guid.NewGuid(),
                Uri: $"file:///repo/doc{i}.cs", Path: $"file:///repo/doc{i}.cs",
                NodeScope: "document", Kind: "document", Symbol: null,
                Lang: "csharp", Mime: "code.csharp",
                Headline: $"Document {i}", Structure: $"class Doc{i} {{ }}",
                Snippet: null, LineStart: null, LineEnd: null,
                BM25Score: 0.3, FuzzyScore: 0.1, SemScore: score,
                Score: score, Confidence: 90 - i * 5,
                SemProvenance: "direct"));
        }
        return candidates;
    }

    private sealed class StubExploreCandidateService(ExploreCandidateResult result) : IExploreCandidateService
    {
        public Task<ExploreCandidateResult> SearchAsync(string? query, string? scope, int k, CancellationToken ct)
            => Task.FromResult(result);
    }

    /// <summary>Stub that reverses document order or keeps it.</summary>
    private sealed class StubRerankProvider(bool reverseOrder) : IRerankProvider
    {
        public bool Enabled => true;
        public int CallCount { get; private set; }

        public Task<RerankResult> RerankAsync(
            string query, IReadOnlyList<RerankDocument> documents,
            int topK = 0, CancellationToken cancellationToken = default)
        {
            CallCount++;
            var results = documents
                .Select((d, i) => new RerankScore(d.Index,
                    reverseOrder ? (float)(i + 1) / documents.Count : (float)(documents.Count - i) / documents.Count))
                .OrderByDescending(r => r.RelevanceScore)
                .ToList();

            return Task.FromResult(new RerankResult(results, 100));
        }
    }

    private sealed class FailingRerankProvider : IRerankProvider
    {
        public bool Enabled => true;

        public Task<RerankResult> RerankAsync(
            string query, IReadOnlyList<RerankDocument> documents,
            int topK = 0, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Reranker unavailable");
    }
}
