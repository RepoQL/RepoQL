using AwesomeAssertions;
using RepoQL.Explore;
using RepoQL.Explore.Search;

namespace RepoQL.Tests;

internal sealed class ExploreOrchestratorInspectRefinementTests
{
    private static readonly IndexerStatus ReadyStatus = new(0, true, true, 12);

    [Test]
    public async Task Inspect_WithKeywords_RendersFocusedRefinedSnippets()
    {
        var searchEngine = new StubSearchEngine(
            [
                CreateDocumentResult("file:///src/AuthService.cs", 94, "Auth service", "class AuthService"),
                CreateDocumentResult("file:///src/Tokens.cs", 82, "Token helpers", "class TokenHelper")
            ]);

        var refinement = new StubInspectRefinementService(
            new InspectRefinementResult(
                Results:
                [
                    new InspectRefinedSnippet(
                        Uri: "file:///src/AuthService.cs",
                        Headline: "Auth service",
                        Snippet: "if (token.IsExpired)\n{\n    return RefreshAsync(token);\n}",
                        LineStart: 143,
                        LineEnd: 149,
                        Lang: "csharp",
                        Score: 0.91)
                ],
                Rounds: 2,
                Widenings: 1,
                FinalCandidateLimit: 192,
                FallbackUsed: false,
                TimedOut: false,
                DegradedReason: null));

        var orchestrator = new ExploreOrchestrator(
            searchEngine,
            jitService: null,
            llmProvider: null,
            inspectRefinementService: refinement,
            inspectRefinementOptions: new InspectRefinementOptions());

        var result = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 3200,
                Intent: Intent.Inspect,
                Scope: null,
                Keywords: "token refresh expiry",
                Boost: null,
                Penalize: null,
                Limit: null),
            ReadyStatus,
            CancellationToken.None);

        refinement.CallCount.Should().Be(1);
        result.RenderedOutput.Should().Contain("Auth service");
        result.RenderedOutput.Should().Contain("if (token.IsExpired)");
        result.RenderedOutput.Should().Contain("```csharp");
    }

    [Test]
    public async Task Locate_WithKeywords_DoesNotInvokeInspectRefinement()
    {
        var searchEngine = new StubSearchEngine(
            [CreateDocumentResult("file:///src/AuthService.cs", 90, "Auth service", "class AuthService")]);
        var refinement = new StubInspectRefinementService(
            new InspectRefinementResult([], 0, 0, 0, false, false, null));

        var orchestrator = new ExploreOrchestrator(
            searchEngine,
            jitService: null,
            llmProvider: null,
            inspectRefinementService: refinement,
            inspectRefinementOptions: new InspectRefinementOptions());

        var result = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 1800,
                Intent: Intent.Locate,
                Scope: null,
                Keywords: "token refresh",
                Boost: null,
                Penalize: null,
                Limit: null),
            ReadyStatus,
            CancellationToken.None);

        refinement.CallCount.Should().Be(0);
    }

    [Test]
    public async Task Inspect_WhenRefinementReturnsNoMatches_FallsBackToStageOneOutput()
    {
        var searchEngine = new StubSearchEngine(
            [CreateDocumentResult("file:///src/AuthService.cs", 90, "Auth service", "class AuthService")]);
        var refinement = new StubInspectRefinementService(
            new InspectRefinementResult([], 1, 0, 96, false, false, "no semantic candidates"));

        var orchestrator = new ExploreOrchestrator(
            searchEngine,
            jitService: null,
            llmProvider: null,
            inspectRefinementService: refinement,
            inspectRefinementOptions: new InspectRefinementOptions());

        var result = await orchestrator.ExecuteAsync(
            new ExploreQuery(
                TokenBudget: 2200,
                Intent: Intent.Inspect,
                Scope: null,
                Keywords: "token refresh",
                Boost: null,
                Penalize: null,
                Limit: null),
            ReadyStatus,
            CancellationToken.None);

        refinement.CallCount.Should().Be(1);
        result.RenderedOutput.Should().Contain("index: ready");
    }

    private static SearchResult CreateDocumentResult(string uri, int confidence, string headline, string structure) =>
        new(
            Uri: uri,
            Scope: SearchScope.Document,
            Kind: null,
            Symbol: null,
            Headline: headline,
            Structure: structure,
            Snippet: null,
            LineStart: null,
            LineEnd: null,
            Lang: "csharp",
            SemanticType: "code.csharp",
            RawScore: confidence / 100.0,
            Confidence: confidence,
            ChildObjects: null);

    private sealed class StubSearchEngine(IReadOnlyList<SearchResult> results) : IExploreSearchEngine
    {
        public Task<SearchEngineResult> SearchAsync(
            SearchParameters parameters,
            IJitObjectSearchService? jitService,
            JitEmbeddingCache? jitCache,
            CancellationToken cancellationToken)
            => Task.FromResult(new SearchEngineResult(
                Results: results,
                TotalDocumentsMatched: results.Count,
                TotalObjectsMatched: 0,
                IndexerStatus: null));
    }

    private sealed class StubInspectRefinementService(InspectRefinementResult response) : IInspectRefinementService
    {
        public int CallCount { get; private set; }

        public Task<InspectRefinementResult> RefineAsync(
            string keywords,
            IReadOnlyList<InspectRefinementCandidate> candidates,
            int tokenBudget,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(response);
        }
    }
}
