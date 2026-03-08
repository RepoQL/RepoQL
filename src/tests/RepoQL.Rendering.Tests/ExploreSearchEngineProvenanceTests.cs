using AwesomeAssertions;
using RepoQL.Explore.Search;

namespace RepoQL.Rendering.Tests;

public class ExploreSearchEngineProvenanceTests
{
    [Test]
    [Arguments(0.9, 0.1, 0.0, 0.0, "semantic")]
    [Arguments(0.1, 0.9, 0.0, 0.0, "name")]
    [Arguments(0.1, 0.0, 0.9, 0.0, "lexical")]
    [Arguments(0.1, 0.0, 0.0, 0.9, "content")]
    [Arguments(1.0, 0.85, 0.0, 0.0, "mixed")]
    [DisplayName("ComputeProvenance returns dominant signal label")]
    public void Given_SignalContributions_When_ComputingProvenance_Then_ReturnsExpectedLabel(
        double semantic,
        double name,
        double regex,
        double chunk,
        string expected)
    {
        ExploreSearchEngine.ComputeProvenance(semantic, name, regex, chunk).Should().Be(expected);
    }

    [Test]
    [DisplayName("ComputeProvenance returns null when all signals are zero")]
    public void Given_ZeroSignals_When_ComputingProvenance_Then_ReturnsNull()
    {
        ExploreSearchEngine.ComputeProvenance(0.0, 0.0, 0.0, 0.0).Should().BeNull();
    }

    [Test]
    [DisplayName("Standard path passes through provenance from SQL")]
    public async Task Given_StandardSearch_When_ScoresAvailable_Then_ResultsIncludeProvenance()
    {
        var documentUri = "file:///repo/sample.cs";
        var docId = Guid.NewGuid();
        var searchEngine = new ExploreSearchEngine(new StubExploreCandidateService(
            new ExploreCandidateResult(
            [
                new ExploreCandidate(
                    DocId: docId,
                    NodeId: Guid.NewGuid(),
                    Uri: documentUri,
                    Path: documentUri,
                    NodeScope: "document",
                    Kind: "document",
                    Symbol: null,
                    Lang: "csharp",
                    Mime: "code.csharp",
                    Headline: "Sample",
                    Structure: null,
                    Snippet: null,
                    LineStart: null,
                    LineEnd: null,
                    BM25Score: 0.2,
                    FuzzyScore: 0.0,
                    SemScore: 0.9,
                    Score: 0.9,
                    Confidence: 88,
                    SemProvenance: "semantic"),
                new ExploreCandidate(
                    DocId: docId,
                    NodeId: Guid.NewGuid(),
                    Uri: $"{documentUri}#symbol=Validate",
                    Path: documentUri,
                    NodeScope: "object",
                    Kind: "cs_method",
                    Symbol: "Validate",
                    Lang: "csharp",
                    Mime: "code.csharp",
                    Headline: "Validate",
                    Structure: null,
                    Snippet: "bool Validate() => true;",
                    LineStart: 10,
                    LineEnd: 11,
                    BM25Score: 0.1,
                    FuzzyScore: 0.95,
                    SemScore: 0.2,
                    Score: 0.8,
                    Confidence: 81,
                    SemProvenance: "name")
            ],
            TotalMatched: 1)));

        var result = await searchEngine.SearchAsync(
            new SearchParameters(
                Scope: null,
                Question: "validate token",
                Patterns: [],
                Breadth: 5,
                TokenBudget: 2000),
            jitService: null,
            jitCache: null,
            cancellationToken: CancellationToken.None);

        result.Results.Should().HaveCount(1);
        var documentResult = result.Results[0];
        documentResult.Provenance.Should().Be("semantic");
        documentResult.ChildObjects.Should().NotBeNull();
        documentResult.ChildObjects![0].Provenance.Should().Be("name");
    }

    [Test]
    [DisplayName("JIT enrichment updates provenance to direct for enriched objects")]
    public async Task Given_JitEnrichment_When_ScoresUpdated_Then_ProvenanceUpdatedToDirect()
    {
        var documentUri = "file:///repo/sample.cs";
        var docId = Guid.NewGuid();
        var objectNodeId = Guid.NewGuid();

        var originalCandidates = new List<ExploreCandidate>
        {
            new(
                DocId: docId,
                NodeId: Guid.NewGuid(),
                Uri: documentUri,
                Path: documentUri,
                NodeScope: "document",
                Kind: "document",
                Symbol: null,
                Lang: "csharp",
                Mime: "code.csharp",
                Headline: "Sample",
                Structure: null,
                Snippet: null,
                LineStart: null,
                LineEnd: null,
                BM25Score: 0.2,
                FuzzyScore: 0.0,
                SemScore: 0.9,
                Score: 0.9,
                Confidence: 88,
                SemProvenance: "semantic"),
            new(
                DocId: docId,
                NodeId: objectNodeId,
                Uri: $"{documentUri}#symbol=Validate",
                Path: documentUri,
                NodeScope: "object",
                Kind: "cs_method",
                Symbol: "Validate",
                Lang: "csharp",
                Mime: "code.csharp",
                Headline: "Validate",
                Structure: null,
                Snippet: "bool Validate() => true;",
                LineStart: 10,
                LineEnd: 11,
                BM25Score: 0.1,
                FuzzyScore: 0.0,
                SemScore: 0.2,
                Score: 0.5,
                Confidence: 60,
                SemProvenance: "inherited")
        };

        // Enrichment updates the object's provenance to "direct" and boosts score
        var enrichedCandidates = originalCandidates.ToList();
        enrichedCandidates[1] = enrichedCandidates[1] with
        {
            Score = 0.85,
            SemScore = 0.9,
            SemProvenance = "direct"
        };

        var searchEngine = new ExploreSearchEngine(new StubExploreCandidateService(
            new ExploreCandidateResult(originalCandidates, TotalMatched: 1)));

        var result = await searchEngine.SearchAsync(
            new SearchParameters(
                Scope: null,
                Question: "validate token",
                Patterns: [],
                Breadth: 5,
                TokenBudget: 2000),
            jitService: new StubJitEnrichmentService(
                new JitEnrichmentResult(enrichedCandidates, ScoresChanged: true)),
            jitCache: new JitEmbeddingCache(),
            cancellationToken: CancellationToken.None);

        result.Results.Should().HaveCount(1);
        var documentResult = result.Results[0];
        documentResult.Provenance.Should().Be("semantic");
        documentResult.ChildObjects.Should().NotBeNull();
        documentResult.ChildObjects![0].Provenance.Should().Be("direct");
    }

    private sealed class StubExploreCandidateService(ExploreCandidateResult result) : IExploreCandidateService
    {
        public Task<ExploreCandidateResult> SearchAsync(
            string? query,
            string? scope,
            int k,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class StubJitEnrichmentService(JitEnrichmentResult result) : IJitObjectSearchService
    {
        public Task<JitEnrichmentResult> EnrichAsync(
            string question,
            IReadOnlyList<ExploreCandidate> candidates,
            JitEmbeddingCache jitCache,
            ObjectSearchConfig config,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }
}
