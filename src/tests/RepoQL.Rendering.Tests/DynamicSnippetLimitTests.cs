using AwesomeAssertions;
using RepoQL.Explore.Search;

namespace RepoQL.Rendering.Tests;

public class DynamicSnippetLimitTests
{
    [Test]
    [Arguments(8, 2000, 1, 3)]
    [Arguments(5, 2000, 1, 5)]
    [Arguments(2, 2000, 1, 13)]
    [Arguments(5, 2000, 1, 5)]
    [DisplayName("CalculateSnippetLimit caps by breadth")]
    public void Given_Breadth_When_CalculatingSnippetLimit_Then_BreadthCapIsApplied(
        int breadth,
        int tokenBudget,
        int resultCount,
        int expected)
    {
        FileGrouper.CalculateSnippetLimit(breadth, tokenBudget, resultCount).Should().Be(expected);
    }

    [Test]
    [DisplayName("CalculateSnippetLimit enforces minimum of 2")]
    public void Given_LowBudget_When_CalculatingSnippetLimit_Then_MinimumIsTwo()
    {
        FileGrouper.CalculateSnippetLimit(5, tokenBudget: 100, resultCount: 10)
            .Should().Be(2);
    }

    [Test]
    [DisplayName("CalculateSnippetLimit uses min(resultCount, 10) denominator")]
    public void Given_HighResultCount_When_CalculatingSnippetLimit_Then_ResultCountIsBoundedToTen()
    {
        FileGrouper.CalculateSnippetLimit(2, tokenBudget: 9000, resultCount: 50)
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
    public async Task Given_StandardSearch_When_BreadthIsBalanced_Then_UsesDynamicSnippetLimit()
    {
        var documentUri = "file:///repo/sample.cs";
        var candidates = CreateCandidates(documentUri, 5);
        var searchEngine = new ExploreSearchEngine(new StubExploreCandidateService(
            new ExploreCandidateResult(candidates, TotalMatched: 1)));

        var result = await searchEngine.SearchAsync(
            new SearchParameters(
                Scope: null,
                Question: "find symbol",
                Patterns: [],
                Breadth: 5,
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
    public async Task Given_JitSearch_When_BreadthIsBalanced_Then_UsesDynamicSnippetLimit()
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
            new StubExploreCandidateService(new ExploreCandidateResult([], TotalMatched: 0)));

        var result = await searchEngine.SearchAsync(
            new SearchParameters(
                Scope: null,
                Question: "find symbol",
                Patterns: [],
                Breadth: 5,
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

    private static List<ExploreCandidate> CreateCandidates(string documentUri, int count)
    {
        var docId = Guid.NewGuid();
        var candidates = new List<ExploreCandidate>
        {
            new(
                DocId: docId,
                NodeId: Guid.NewGuid(),
                Uri: documentUri,
                Path: documentUri,
                NodeScope: "document",
                Kind: "document",
                Symbol: null,
                Lang: "cs",
                Mime: "code",
                Headline: "Doc",
                Structure: null,
                Snippet: null,
                LineStart: null,
                LineEnd: null,
                BM25Score: 0,
                FuzzyScore: 0,
                SemScore: 0,
                Score: 100,
                Confidence: 80,
                SemProvenance: "direct")
        };

        candidates.AddRange(Enumerable.Range(0, count)
            .Select(i => new ExploreCandidate(
                DocId: docId,
                NodeId: Guid.NewGuid(),
                Uri: $"{documentUri}#symbol=Method{i}",
                Path: documentUri,
                NodeScope: "object",
                Kind: "cs_method",
                Symbol: $"Method{i}",
                Lang: "cs",
                Mime: "code",
                Headline: $"Method {i}",
                Structure: null,
                Snippet: $"snippet {i}",
                LineStart: i + 1,
                LineEnd: i + 2,
                BM25Score: 0,
                FuzzyScore: 0,
                SemScore: 0,
                Score: count - i,
                Confidence: 80 - i,
                SemProvenance: "direct")));

        return candidates;
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
