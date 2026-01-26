using System.Linq;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class TreeHandlerTests
{
    [Test]
    public async Task UsesHeadlinesWhenBudgetAllows()
    {
        var baseTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var namesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(baseTree, namesTree, foldersTree));

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Be(baseTree);
        result.Metadata.FilesConsulted.Should().BeEquivalentTo(new[] { "file:///src/a.cs" });
        result.Metadata.Warning.Should().BeNull();
        result.ExceedsBudget.Should().BeFalse();
        result.Metadata.Extra["verbosity"].Should().Be("headlines");
    }

    [Test]
    public async Task FallsBackToNamesWhenHeadlinesExceedBudget()
    {
        var baseTree = "file:///\n└── src/\n    └── a.cs | " + headline;
        var namesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var headline = string.Join(' ', Enumerable.Repeat("Alpha", 20));
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, headline, null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(baseTree, namesTree, foldersTree));

        var namesTree = "file:///\n└── src/\n    └── a.cs";
        var headlinesTree = baseTree;
        var namesTokens = TokenEstimator.EstimateTokens(namesTree);
        var headlinesTokens = TokenEstimator.EstimateTokens(headlinesTree);
        headlinesTokens.Should().BeGreaterThan(namesTokens);

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: headlinesTokens - 1, CancellationToken.None);

        result.Content.Should().Be(namesTree);
        result.Metadata.Extra["verbosity"].Should().Be("names");
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task FallsBackToFoldersWhenNamesExceedBudget()
    {
        var baseTree = "file:///\n└── src/\n    ├── a.cs | Alpha\n    ├── b.cs | Beta\n    └── c.cs | Gamma";
        var namesTree = "file:///\n└── src/\n    ├── a.cs\n    ├── b.cs\n    └── c.cs";
        var foldersTree = "file:///\n└── src/ (3 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null),
            new ReadDocument("file:///src/b.cs", null, null, "Beta", null, null),
            new ReadDocument("file:///src/c.cs", null, null, "Gamma", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(baseTree, namesTree, foldersTree));

        var namesTree = "file:///\n└── src/\n    ├── a.cs\n    ├── b.cs\n    └── c.cs";
        var namesTokens = TokenEstimator.EstimateTokens(namesTree);
        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        namesTokens.Should().BeGreaterThan(foldersTokens);

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: foldersTokens, CancellationToken.None);

        result.Content.Should().Be(foldersTree);
        result.Metadata.Extra["verbosity"].Should().Be("folders");
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task MarksExceedsBudgetWhenFoldersTooLarge()
    {
        var baseTree = "file:///\n└── src/\n    ├── a.cs | Alpha\n    ├── b.cs | Beta\n    └── c.cs | Gamma";
        var namesTree = "file:///\n└── src/\n    ├── a.cs\n    ├── b.cs\n    └── c.cs";
        var foldersTree = "file:///\n└── src/ (3 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null),
            new ReadDocument("file:///src/b.cs", null, null, "Beta", null, null),
            new ReadDocument("file:///src/c.cs", null, null, "Gamma", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(baseTree, namesTree, foldersTree));

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        var result = await handler.ExecuteAsync(documents, null, tokenBudget: foldersTokens - 1, CancellationToken.None);

        result.Content.Should().Be(foldersTree);
        result.ExceedsBudget.Should().BeTrue();
    }

    private sealed class StubContentProvider : IReadContentProvider
    {
        private readonly string _tree;
        private readonly string _namesTree;
        private readonly string _foldersTree;

        public StubContentProvider(string tree, string namesTree, string foldersTree)
        {
            _tree = tree;
            _namesTree = namesTree;
            _foldersTree = foldersTree;
        }

        public Task<ReadDocument?> FetchDocumentAsync(string uri, CancellationToken cancellationToken)
            => Task.FromResult<ReadDocument?>(null);

        public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string globUri, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReadDocument>>([]);

        public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, bool includeHeadlines, CancellationToken cancellationToken)
            => Task.FromResult<string?>(foldersOnly ? _foldersTree : (includeHeadlines ? _tree : _namesTree));
    }
}


