using AwesomeAssertions;
using RepoQL.Explore;
using RepoQL.Explore.Search;

namespace RepoQL.Rendering.Tests;

public class DynamicSnippetLimitTests
{
    [Test]
    [Arguments(Intent.Inventory, 2000, 1, 3)]
    [Arguments(Intent.Locate, 2000, 1, 5)]
    [Arguments(Intent.Inspect, 2000, 1, 13)]
    [Arguments(Intent.Explain, 2000, 1, 8)]
    [DisplayName("CalculateSnippetLimit caps by intent")]
    public void Given_Intent_When_CalculatingSnippetLimit_Then_IntentCapIsApplied(
        Intent intent,
        int tokenBudget,
        int resultCount,
        int expected)
    {
        FileGrouper.CalculateSnippetLimit(intent, tokenBudget, resultCount).Should().Be(expected);
    }

    [Test]
    [DisplayName("CalculateSnippetLimit enforces minimum of 2")]
    public void Given_LowBudget_When_CalculatingSnippetLimit_Then_MinimumIsTwo()
    {
        FileGrouper.CalculateSnippetLimit(Intent.Locate, tokenBudget: 100, resultCount: 10)
            .Should().Be(2);
    }

    [Test]
    [DisplayName("CalculateSnippetLimit uses min(resultCount, 10) denominator")]
    public void Given_HighResultCount_When_CalculatingSnippetLimit_Then_ResultCountIsBoundedToTen()
    {
        FileGrouper.CalculateSnippetLimit(Intent.Inspect, tokenBudget: 9000, resultCount: 50)
            .Should().Be(6);
    }

    [Test]
    [DisplayName("Group uses configured max snippets per file")]
    public void Given_CustomSnippetLimit_When_Grouping_Then_UsesProvidedLimit()
    {
        var documentUri = "file:///repo/sample.cs";
        var documents = new List<DocumentMatch>
        {
            new(documentUri, "Doc", null, null, "cs", null, 100)
        };
        var objects = CreateObjectMatches(documentUri, 5);

        var groups = FileGrouper.Group(documents, objects, maxSnippetsPerFile: 2);

        groups.Should().HaveCount(1);
        groups[0].SnippetObjects.Should().HaveCount(2);
        groups[0].HeadlineObjects.Should().HaveCount(3);
    }

    [Test]
    [DisplayName("Group defaults to 3 snippets per file for backward compatibility")]
    public void Given_NoSnippetLimit_When_Grouping_Then_DefaultsToThree()
    {
        var documentUri = "file:///repo/sample.cs";
        var documents = new List<DocumentMatch>
        {
            new(documentUri, "Doc", null, null, "cs", null, 100)
        };
        var objects = CreateObjectMatches(documentUri, 5);

        var groups = FileGrouper.Group(documents, objects);

        groups.Should().HaveCount(1);
        groups[0].SnippetObjects.Should().HaveCount(3);
        groups[0].HeadlineObjects.Should().HaveCount(2);
    }

    [Test]
    [DisplayName("Standard search path uses dynamic snippet limit")]
    public async Task Given_StandardSearch_When_IntentLocate_Then_UsesDynamicSnippetLimit()
    {
        var documentUri = "file:///repo/sample.cs";
        var documents = new List<DocumentMatch>
        {
            new(documentUri, "Doc", null, null, "cs", null, 100)
        };
        var objects = CreateObjectMatches(documentUri, 5);

        var documentSearch = new StubDocumentSearchService(
            new DocumentSearchResult(documents, new Dictionary<string, IReadOnlyList<ChunkScore>>()));
        var objectSearch = new StubObjectSearchService(objects);
        var searchEngine = new ExploreSearchEngine(documentSearch, objectSearch);

        var result = await searchEngine.SearchAsync(
            new SearchParameters(
                Scope: null,
                Question: "find symbol",
                Patterns: [],
                Intent: Intent.Locate,
                TokenBudget: 2000),
            jitService: null,
            jitCache: null,
            cancellationToken: CancellationToken.None);

        result.Results.Should().HaveCount(1);
        var document = result.Results[0];
        document.Scope.Should().Be(SearchScope.Document);
        document.ChildObjects.Should().NotBeNull();
        document.ChildObjects!.Count(c => c.Snippet is not null).Should().Be(5);
    }

    [Test]
    [DisplayName("JIT search path uses dynamic snippet limit")]
    public async Task Given_JitSearch_When_IntentLocate_Then_UsesDynamicSnippetLimit()
    {
        var documentUri = "file:///repo/sample.cs";
        var jitResult = new JitObjectSearchResult(
            SelectedDocuments:
            [
                new DocumentExpansionCandidate(
                    DocumentUri: documentUri,
                    DocumentScore: 100,
                    SoftmaxProbability: 1.0,
                    CumulativeProbability: 1.0,
                    Headline: "Doc",
                    Structure: null,
                    Lang: "cs",
                    SemanticType: "code",
                    Source: "hybrid",
                    SemanticScore: 100,
                    Bm25Score: 100,
                    StructMentions: 0,
                    BodyMentions: 0,
                    HighScoringChunks: [])
            ],
            ScoredObjects: CreateJitObjects(documentUri, 6),
            QuerySignals: new NormalizedQuerySignals
            {
                RawQuery = "find symbol",
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
                Question: "find symbol",
                Patterns: [],
                Intent: Intent.Locate,
                TokenBudget: 2000),
            jitService: new StubJitObjectSearchService(jitResult),
            jitCache: new JitEmbeddingCache(),
            cancellationToken: CancellationToken.None);

        result.Results.Should().HaveCount(1);
        var document = result.Results[0];
        document.Scope.Should().Be(SearchScope.Document);
        document.ChildObjects.Should().NotBeNull();
        document.ChildObjects!.Count(c => c.Snippet is not null).Should().Be(5);
        document.ChildObjects.Count(c => c.Snippet is null).Should().Be(1);
    }

    private static List<ObjectMatch> CreateObjectMatches(string documentUri, int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new ObjectMatch(
                Uri: $"{documentUri}#symbol=Method{i}",
                DocumentUri: documentUri,
                Kind: "cs_method",
                Symbol: $"Method{i}",
                Headline: $"Method {i}",
                Structure: null,
                Snippet: $"snippet {i}",
                LineStart: i + 1,
                LineEnd: i + 2,
                Lang: "cs",
                SemanticType: "code",
                Score: count - i))
            .ToList();
    }

    private static List<ObjectCandidate> CreateJitObjects(string documentUri, int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new ObjectCandidate
            {
                NodeId = $"node-{i}",
                Uri = $"{documentUri}#symbol=Method{i}",
                DocumentUri = documentUri,
                Kind = "cs_method",
                Symbol = $"Method{i}",
                Headline = $"Method {i}",
                Structure = null,
                Body = $"body {i}",
                Snippet = $"snippet {i}",
                LineStart = i + 1,
                LineEnd = i + 2,
                StartByte = null,
                EndByte = null,
                Lang = "cs",
                SemanticType = "code",
                FinalScore = count - i,
                Confidence = 80 - i
            })
            .ToList();
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
