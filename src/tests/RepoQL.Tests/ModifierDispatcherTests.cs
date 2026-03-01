using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.Tests;

internal sealed class ModifierDispatcherTests
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
    public async Task ReturnsNullWhenNoModifierSyntax()
    {
        var dispatcher = new ModifierDispatcher(new StubContentProvider(), [new StubModifierHandler("headline")]);

        var result = await dispatcher.TryExecuteAsync(
            "file:///repo/notes.md",
            tokenBudget: 200,
            status: DefaultStatus,
            cancellationToken: CancellationToken.None);

        result.Should().BeNull();
    }

    [Test]
    public async Task ReturnsErrorForUnknownModifier()
    {
        var dispatcher = new ModifierDispatcher(new StubContentProvider(), [new StubModifierHandler("headline")]);

        var result = await dispatcher.TryExecuteAsync(
            "file:///repo/notes.md => unknown",
            tokenBudget: 200,
            status: DefaultStatus,
            cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.Success.Should().BeFalse();
        result.Error.Should().Contain("Available modifiers: headline");
    }

    [Test]
    public async Task PassesParameterToHandler()
    {
        var handler = new StubModifierHandler("question");
        var dispatcher = new ModifierDispatcher(new StubContentProvider(), [handler]);

        var result = await dispatcher.TryExecuteAsync(
            "file:///repo/notes.md => question:   What is this?   ",
            tokenBudget: 200,
            status: DefaultStatus,
            cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        handler.LastParameter.Should().Be("What is this?");
    }

    [Test]
    public async Task ReturnsCachedResultOnRepeatRequest()
    {
        var handler = new StubModifierHandler(
            "headline",
            _ => new ModifierResult(
                "HELLO",
                TokenCount: 1200,
                TotalAvailable: 1,
                Shown: 1,
                ExceedsBudget: false,
                Metadata: new ResultMetadata([], null, new Dictionary<string, object>())));

        var dispatcher = new ModifierDispatcher(new StubContentProvider(), [handler]);
        var pattern = $"file:///repo/{Guid.NewGuid():N}.md";
        var input = $"{pattern} => headline";

        var first = await dispatcher.TryExecuteAsync(
            input,
            tokenBudget: 50,
            status: DefaultStatus,
            cancellationToken: CancellationToken.None);

        first.Should().NotBeNull();
        first!.RenderedOutput.Should().Contain("Repeat request to proceed");

        var second = await dispatcher.TryExecuteAsync(
            input,
            tokenBudget: 50,
            status: DefaultStatus,
            cancellationToken: CancellationToken.None);

        second.Should().NotBeNull();
        second!.RenderedOutput.Should().Contain("HELLO");
        second.RenderedOutput.Should().Contain("budget override");
    }

    [Test]
    public async Task WithinToleranceBandPassesThroughWithoutConfirmation()
    {
        // 110 tokens on a 100-token budget = 10% over, within 15% tolerance
        var handler = new StubModifierHandler(
            "headline",
            _ => new ModifierResult(
                "content within tolerance",
                TokenCount: 110,
                TotalAvailable: 1,
                Shown: 1,
                ExceedsBudget: true, // handler says exceeds, but gate should allow it
                Metadata: new ResultMetadata([], null, new Dictionary<string, object>())));

        var dispatcher = new ModifierDispatcher(new StubContentProvider(), [handler]);
        var pattern = $"file:///repo/{Guid.NewGuid():N}.md";

        var result = await dispatcher.TryExecuteAsync(
            $"{pattern} => headline",
            tokenBudget: 100,
            status: DefaultStatus,
            cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.RenderedOutput.Should().Contain("content within tolerance");
        result.RenderedOutput.Should().NotContain("Repeat request to proceed");
    }

    [Test]
    public async Task BeyondToleranceBandTriggersConfirmation()
    {
        // 120 tokens on a 100-token budget = 20% over, beyond 15% tolerance
        var handler = new StubModifierHandler(
            "headline",
            _ => new ModifierResult(
                "content beyond tolerance",
                TokenCount: 120,
                TotalAvailable: 1,
                Shown: 1,
                ExceedsBudget: true,
                Metadata: new ResultMetadata([], null, new Dictionary<string, object>())));

        var dispatcher = new ModifierDispatcher(new StubContentProvider(), [handler]);
        var pattern = $"file:///repo/{Guid.NewGuid():N}.md";

        var result = await dispatcher.TryExecuteAsync(
            $"{pattern} => headline",
            tokenBudget: 100,
            status: DefaultStatus,
            cancellationToken: CancellationToken.None);

        result.Should().NotBeNull();
        result!.RenderedOutput.Should().Contain("Repeat request to proceed");
    }

    private sealed class StubContentProvider : IReadContentProvider
    {
        public Task<ReadDocument?> FetchDocumentAsync(string uri, CancellationToken cancellationToken)
            => Task.FromResult<ReadDocument?>(new ReadDocument(uri, "content", "text/plain", "headline", null, null));

        public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string globUri, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReadDocument>>([
                new ReadDocument(globUri, "content", "text/plain", "headline", null, null)
            ]);
    }

    private sealed class StubModifierHandler : IModifierHandler
    {
        private readonly Func<string?, ModifierResult> _resultFactory;

        public StubModifierHandler(string modifierName)
            : this(modifierName, _ => new ModifierResult(
                "OK",
                TokenCount: 10,
                TotalAvailable: 1,
                Shown: 1,
                ExceedsBudget: false,
                Metadata: new ResultMetadata([], null, new Dictionary<string, object>())))
        {
        }

        public StubModifierHandler(string modifierName, Func<string?, ModifierResult> resultFactory)
        {
            ModifierName = modifierName;
            _resultFactory = resultFactory;
        }

        public string ModifierName { get; }

        public string? LastParameter { get; private set; }

        public bool CanHandle(string? modifier)
            => string.Equals(modifier, ModifierName, StringComparison.OrdinalIgnoreCase);

        public Task<ModifierResult> ExecuteAsync(
            IReadOnlyList<ReadDocument> documents,
            string? parameter,
            int tokenBudget,
            CancellationToken ct)
        {
            LastParameter = parameter;
            return Task.FromResult(_resultFactory(parameter));
        }
    }
}
