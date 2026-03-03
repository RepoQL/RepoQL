using AwesomeAssertions;
using RepoQL.Explore;
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
    [DisplayName("Standard path assigns provenance from document and object score components")]
    public async Task Given_StandardSearch_When_ScoresAvailable_Then_ResultsIncludeProvenance()
    {
        var documentUri = "file:///repo/sample.cs";
        var documentSearch = new StubDocumentSearchService(
            new DocumentSearchResult(
            [
                new DocumentMatch(
                    Uri: documentUri,
                    Headline: "Sample",
                    Structure: null,
                    Snippet: null,
                    Lang: "csharp",
                    SemanticType: "code.csharp",
                    Score: 0.9,
                    SemanticScore: 0.9,
                    NameHitScore: 0.0,
                    RegexHitScore: 0.1,
                    ChunkOverlapScore: 0.2)
            ],
            new Dictionary<string, IReadOnlyList<ChunkScore>>()));

        var objectSearch = new StubObjectSearchService(
        [
            new ObjectMatch(
                Uri: $"{documentUri}#symbol=Validate",
                DocumentUri: documentUri,
                Kind: "cs_method",
                Symbol: "Validate",
                Headline: "Validate",
                Structure: null,
                Snippet: "bool Validate() => true;",
                LineStart: 10,
                LineEnd: 11,
                Lang: "csharp",
                SemanticType: "code.csharp",
                Score: 0.8,
                SemanticScore: 0.2,
                NameHitScore: 0.95,
                RegexHitScore: 0.1,
                ChunkOverlapScore: 0.1)
        ]);

        var searchEngine = new ExploreSearchEngine(documentSearch, objectSearch);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(
                Scope: null,
                Question: "validate token",
                Patterns: [],
                Intent: Intent.Locate,
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
    [DisplayName("JIT path assigns provenance from object candidate signals")]
    public async Task Given_JitSearch_When_ScoresAvailable_Then_ResultsIncludeProvenance()
    {
        var documentUri = "file:///repo/sample.cs";
        var jitResult = new JitObjectSearchResult(
            SelectedDocuments:
            [
                new DocumentExpansionCandidate(
                    DocumentUri: documentUri,
                    DocumentScore: 0.9,
                    SoftmaxProbability: 1.0,
                    CumulativeProbability: 1.0,
                    Headline: "Sample",
                    Structure: null,
                    Lang: "csharp",
                    SemanticType: "code.csharp",
                    Source: "hybrid",
                    SemanticScore: 0.2,
                    Bm25Score: 0.9,
                    StructMentions: 0,
                    BodyMentions: 0,
                    HighScoringChunks: [])
            ],
            ScoredObjects:
            [
                new ObjectCandidate
                {
                    NodeId = "n1",
                    Uri = $"{documentUri}#symbol=Validate",
                    DocumentUri = documentUri,
                    Kind = "cs_method",
                    Symbol = "Validate",
                    Headline = "Validate",
                    Structure = null,
                    Body = "bool Validate() => true;",
                    Snippet = "bool Validate() => true;",
                    LineStart = 10,
                    LineEnd = 11,
                    StartByte = null,
                    EndByte = null,
                    Lang = "csharp",
                    SemanticType = "code.csharp",
                    SemanticScore = 0.95,
                    NameHitScore = 0.2,
                    RegexHitScore = 0.1,
                    ChunkOverlapScore = 0.1,
                    FinalScore = 0.95,
                    Confidence = 90
                }
            ],
            QuerySignals: new NormalizedQuerySignals
            {
                RawQuery = "validate token",
                QueryEmbedding = null,
                BoostPatterns = [],
                NegativePattern = null,
                QueryTokensLower = new HashSet<string>(),
                BoostRegex = string.Empty,
                DetectedIntent = QueryIntent.Semantic,
                SoftmaxTemperature = 0.5
            });

        var searchEngine = new ExploreSearchEngine(
            new StubDocumentSearchService(new DocumentSearchResult([], new Dictionary<string, IReadOnlyList<ChunkScore>>())),
            new StubObjectSearchService([]));

        var result = await searchEngine.SearchAsync(
            new SearchParameters(
                Scope: null,
                Question: "validate token",
                Patterns: [],
                Intent: Intent.Locate,
                TokenBudget: 2000),
            jitService: new StubJitObjectSearchService(jitResult),
            jitCache: new JitEmbeddingCache(),
            cancellationToken: CancellationToken.None);

        result.Results.Should().HaveCount(1);
        var documentResult = result.Results[0];
        documentResult.Provenance.Should().Be("content");
        documentResult.ChildObjects.Should().NotBeNull();
        documentResult.ChildObjects![0].Provenance.Should().Be("semantic");
    }

    private sealed class StubDocumentSearchService(DocumentSearchResult result) : IDocumentSearchService
    {
        public Task<DocumentSearchResult> SearchAsync(
            string? scope,
            string? question,
            int limit,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class StubObjectSearchService(IReadOnlyList<ObjectMatch> objects) : IObjectSearchService
    {
        public Task<IReadOnlyList<ObjectMatch>> SearchInDocumentsAsync(
            IReadOnlyList<string> documentUris,
            string? question,
            int objectsPerDocument,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(objects);
        }
    }

    private sealed class StubJitObjectSearchService(JitObjectSearchResult result) : IJitObjectSearchService
    {
        public Task<JitObjectSearchResult> SearchAsync(
            string? question,
            string? scope,
            string? boostPattern,
            string? penalizePattern,
            ObjectSearchConfig config,
            JitEmbeddingCache jitCache,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }
}
