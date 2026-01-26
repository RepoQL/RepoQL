using AwesomeAssertions;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class ModifierDispatcherTests
{
    [Test]
    public async Task ReturnsNullWhenNoModifierSyntax()
    {
        var dispatcher = new ModifierDispatcher(new StubContentProvider(), [new StubModifierHandler("headline")]);

        var result = await dispatcher.TryExecuteAsync(
            "file:///repo/notes.md",
            tokenBudget: 200,
            status: new IndexerStatus(0, true, true, 0),
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
            status: new IndexerStatus(0, true, true, 0),
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
            status: new IndexerStatus(0, true, true, 0),
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
            status: new IndexerStatus(0, true, true, 0),
            cancellationToken: CancellationToken.None);

        first.Should().NotBeNull();
        first!.RenderedOutput.Should().Contain("Repeat request to proceed");

        var second = await dispatcher.TryExecuteAsync(
            input,
            tokenBudget: 50,
            status: new IndexerStatus(0, true, true, 0),
            cancellationToken: CancellationToken.None);

        second.Should().NotBeNull();
        second!.RenderedOutput.Should().Contain("HELLO");
        second.RenderedOutput.Should().Contain("budget override");
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
