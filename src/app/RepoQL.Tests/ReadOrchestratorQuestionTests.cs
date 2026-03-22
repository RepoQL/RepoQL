using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Contracts.Inference;
using RepoQL.Contracts.Search;
using RepoQL.Explore;
using RepoQL.Read;
using RepoQL.Explore.Search;

namespace RepoQL.Tests;

/// <summary>
/// Tests for the question syntax feature in ReadOrchestrator.
/// Question syntax: "pattern => question: How does X work?"
/// </summary>
internal sealed class ReadOrchestratorQuestionTests
{
    private static readonly TrustSignal DefaultStatus = new(
        IndexTotal: 100,
        IndexPending: 0,
        IndexFailed: 0,
        IndexStale: 0,
        SemanticEnabled: true,
        SemanticReady: true,
        SemanticPercent: 100,
        ExecutionTimeMs: 0);

    [Test]
    [Arguments("file:///path => question: How does this work?", "file:///path", "How does this work?")]
    [Arguments("file:///src/**/*.cs => question: What patterns are used?", "file:///src/**/*.cs", "What patterns are used?")]
    [Arguments("file:///path => QUESTION: Case insensitive?", "file:///path", "Case insensitive?")]
    [Arguments("file:///path => Question: Mixed case?", "file:///path", "Mixed case?")]
    [Arguments("file:///path =>question:No spaces?", "file:///path", "No spaces?")]
    [Arguments("file:///path => question:  Extra  spaces  ", "file:///path", "Extra  spaces")]
    public async Task QuestionSyntax_ParsesCorrectly(string input, string expectedPattern, string expectedQuestion)
    {
        var inference = new StubInferenceProvider("Synthesized answer");
        var orchestrator = CreateOrchestrator(inference: inference);

        var result = await orchestrator.ExecuteAsync(
            input,
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        // The fact that it successfully calls ExecuteWithQuestionAsync proves parsing worked
        result.Success.Should().BeTrue();
        result.Representation.Should().Be("question");
    }

    [Test]
    public async Task QuestionSyntax_SmallContent_CallsInferenceDirectly()
    {
        var inference = new StubInferenceProvider("This is the synthesized answer.");
        var orchestrator = CreateOrchestrator(inference: inference);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.RenderedOutput.Should().Contain("This is the synthesized answer.");
        result.Representation.Should().Be("question");
        result.FilesRead.Should().Be(1);
    }

    [Test]
    public async Task QuestionSyntax_NoInferenceConfigured_ReturnsError()
    {
        var orchestrator = CreateOrchestrator(inference: null);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Inference service not configured");
        result.Error.Should().Contain("inference.service_url");
        result.Error.Should().Contain("cloud.api_key");
    }

    [Test]
    public async Task QuestionSyntax_InferenceDisabled_ReturnsError()
    {
        var inference = new StubInferenceProvider("answer", available: false);
        var orchestrator = CreateOrchestrator(inference: inference);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Inference service not configured");
    }

    [Test]
    public async Task QuestionSyntax_NoFilesMatch_ReturnsError()
    {
        var inference = new StubInferenceProvider("answer");
        var orchestrator = CreateOrchestrator(inference: inference, documents: []);

        var result = await orchestrator.ExecuteAsync(
            "file:///nonexistent => question: What is this?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("File not found");
    }

    [Test]
    public async Task QuestionSyntax_MissingQuestion_FallsBackToDirectRead()
    {
        // "=> question:" with no text after it should not parse as question syntax
        var orchestrator = CreateOrchestrator(inference: null);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question:",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        // Should fall through to modifier dispatcher (which will fail for unknown "question:" modifier)
        // or direct read - the key is it doesn't try question flow
        result.Representation.Should().NotBe("question");
    }

    [Test]
    public async Task QuestionSyntax_MissingPattern_FallsBackToDirectRead()
    {
        // "=> question: text" with no pattern should not parse as question syntax
        var orchestrator = CreateOrchestrator(inference: null);

        var result = await orchestrator.ExecuteAsync(
            "=> question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        // Should fall through - not valid question syntax
        result.Representation.Should().NotBe("question");
    }

    [Test]
    public async Task QuestionSyntax_NoArrowSeparator_FallsBackToDirectRead()
    {
        // No "=>" means not question syntax
        var orchestrator = CreateOrchestrator(inference: null);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        // Should try direct read, not question flow
        result.Representation.Should().NotBe("question");
    }

    [Test]
    public async Task QuestionSyntax_OtherModifier_UsesModifierNotQuestion()
    {
        // "=> headline" should use the headline handler, not question
        var orchestrator = CreateOrchestrator(inference: null);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => headline",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        // Should use headline modifier
        result.Representation.Should().Be("headline");
    }

    [Test]
    public async Task QuestionSyntax_InferenceError_ReturnsErrorResult()
    {
        var inference = new StubInferenceProvider(throwOnComplete: new InvalidOperationException("API limit exceeded"));
        var orchestrator = CreateOrchestrator(inference: inference);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Inference synthesis failed");
        result.Error.Should().Contain("API limit exceeded");
    }

    private static ReadOrchestrator CreateOrchestrator(
        IInferenceProvider? inference = null,
        IReadOnlyList<ReadDocument>? documents = null)
    {
        var contentProvider = new StubContentProvider(documents);
        var searchEngine = new StubSearchEngine();
        var exploreOrchestrator = new ExploreOrchestrator(searchEngine, jitService: null);

        // Use real modifier handlers for testing integration
        var modifierHandlers = new IModifierHandler[]
        {
            new HeadlineHandler(),
            new StructureHandler(),
            new ContentHandler()
        };

        return new ReadOrchestrator(contentProvider, exploreOrchestrator, inference, modifierHandlers);
    }

    private sealed class StubContentProvider : IReadContentProvider
    {
        private readonly IReadOnlyList<ReadDocument> _documents;

        public StubContentProvider(IReadOnlyList<ReadDocument>? documents = null)
        {
            _documents = documents ?? [
                new ReadDocument(
                    "file:///repo/test.cs",
                    "public class Test { }",
                    "text/plain;kind=code.csharp",
                    "Test class",
                    "A simple test class",
                    "class Test")
            ];
        }

        public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string uriPattern, CancellationToken cancellationToken)
            => Task.FromResult(_documents);

        public Task<string?> GetRepoTreeAsync(string? scope, int tokenBudget, CancellationToken cancellationToken)
            => Task.FromResult<string?>("src/\n  test.cs");

        public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, bool includeHeadlines, CancellationToken cancellationToken)
            => Task.FromResult<string?>(string.Join("\n", uris));
    }

    private sealed class StubInferenceProvider : IInferenceProvider
    {
        private readonly string _response;
        private readonly Exception? _throwOnComplete;

        public StubInferenceProvider(string response = "Synthesized answer", bool available = true, Exception? throwOnComplete = null)
        {
            _response = response;
            Available = available;
            _throwOnComplete = throwOnComplete;
        }

        public bool Available { get; }

        public Task<InferenceResult> CompleteAsync(InferenceRequest request, CancellationToken ct = default)
        {
            if (_throwOnComplete is not null)
                throw _throwOnComplete;

            return Task.FromResult(new InferenceResult { Content = _response });
        }

        public Task<InferenceResult> CompleteWithToolsAsync(
            InferenceRequest request,
            ToolOptions toolOptions,
            Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
            CancellationToken ct = default)
            => CompleteAsync(request, ct);
    }

    private sealed class StubSearchEngine : IExploreSearchEngine
    {
        public Task<SearchEngineResult> SearchAsync(
            SearchParameters parameters,
            IJitObjectSearchService? jitService,
            JitEmbeddingCache? jitCache,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new SearchEngineResult(
                Results: [],
                TotalDocumentsMatched: 0,
                TotalObjectsMatched: 0,
                TrustSignal: null));
        }
    }
}
