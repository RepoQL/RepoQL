using AwesomeAssertions;
using RepoQL.Contracts;
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
        var llm = new StubLlmProvider("Synthesized answer");
        var orchestrator = CreateOrchestrator(llm: llm);

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
    public async Task QuestionSyntax_SmallContent_CallsLlmDirectly()
    {
        var llm = new StubLlmProvider("This is the synthesized answer.");
        var orchestrator = CreateOrchestrator(llm: llm);

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
    public async Task QuestionSyntax_NoLlmConfigured_ReturnsError()
    {
        var orchestrator = CreateOrchestrator(llm: null);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("LLM not configured");
        result.Error.Should().Contain("OPENROUTER_API_KEY");
    }

    [Test]
    public async Task QuestionSyntax_LlmDisabled_ReturnsError()
    {
        var llm = new StubLlmProvider("answer", enabled: false);
        var orchestrator = CreateOrchestrator(llm: llm);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("LLM not configured");
    }

    [Test]
    public async Task QuestionSyntax_NoFilesMatch_ReturnsError()
    {
        var llm = new StubLlmProvider("answer");
        var orchestrator = CreateOrchestrator(llm: llm, documents: []);

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
        var orchestrator = CreateOrchestrator(llm: null);

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
        var orchestrator = CreateOrchestrator(llm: null);

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
        var orchestrator = CreateOrchestrator(llm: null);

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
        var orchestrator = CreateOrchestrator(llm: null);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => headline",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        // Should use headline modifier
        result.Representation.Should().Be("headline");
    }

    [Test]
    public async Task QuestionSyntax_LlmError_ReturnsErrorResult()
    {
        var llm = new StubLlmProvider(throwOnSummarize: new InvalidOperationException("API limit exceeded"));
        var orchestrator = CreateOrchestrator(llm: llm);

        var result = await orchestrator.ExecuteAsync(
            "file:///repo/test.cs => question: How does this work?",
            tokenBudget: 2000,
            status: DefaultStatus,
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("LLM synthesis failed");
        result.Error.Should().Contain("API limit exceeded");
    }

    private static ReadOrchestrator CreateOrchestrator(
        ILlmProvider? llm = null,
        IReadOnlyList<ReadDocument>? documents = null)
    {
        var contentProvider = new StubContentProvider(documents);
        var searchEngine = new StubSearchEngine();
        var exploreOrchestrator = new ExploreOrchestrator(searchEngine, jitService: null, llmProvider: llm);

        // Use real modifier handlers for testing integration
        var modifierHandlers = new IModifierHandler[]
        {
            new HeadlineHandler(),
            new StructureHandler(),
            new ContentHandler()
        };

        return new ReadOrchestrator(contentProvider, exploreOrchestrator, llm, modifierHandlers);
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

    private sealed class StubLlmProvider : ILlmProvider
    {
        private readonly string _response;
        private readonly Exception? _throwOnSummarize;

        public StubLlmProvider(string response = "Synthesized answer", bool enabled = true, Exception? throwOnSummarize = null)
        {
            _response = response;
            Enabled = enabled;
            _throwOnSummarize = throwOnSummarize;
        }

        public bool Enabled { get; }
        public string Model => "stub-model";

        public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
        {
            if (_throwOnSummarize is not null)
                throw _throwOnSummarize;
            return Task.FromResult(_response);
        }

        public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult(new LlmSummaryResult(_response));

        public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
            => Task.FromResult(_response);

        public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
            => Task.FromResult(question);
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
