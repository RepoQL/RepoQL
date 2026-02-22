using System.Linq;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class TreeHandlerTests
{
    [Test]
    public void CanHandleMatchesTreeExactly()
    {
        var handler = new TreeHandler(new StubContentProvider("", "", ""));

        handler.CanHandle("tree").Should().BeTrue();
        handler.CanHandle("TREE").Should().BeTrue();
        handler.CanHandle("Tree").Should().BeTrue();
        handler.CanHandle("tree: folders").Should().BeFalse(); // parameter is parsed separately by dispatcher
        handler.CanHandle("notree").Should().BeFalse();
        handler.CanHandle(null).Should().BeFalse();
        handler.CanHandle("").Should().BeFalse();
    }

    [Test]
    public async Task DefaultShowsFilesNotHeadlines()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Be(filesTree);
        result.Metadata.Extra["verbosity"].Should().Be("files");
        result.Metadata.Warning.Should().BeNull();
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task HeadlinesParameterShowsHeadlines()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var result = await handler.ExecuteAsync(documents, "headlines", tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Be(headlinesTree);
        result.Metadata.FilesConsulted.Should().BeEquivalentTo(new[] { "file:///src/a.cs" });
        result.Metadata.Warning.Should().BeNull();
        result.ExceedsBudget.Should().BeFalse();
        result.Metadata.Extra["verbosity"].Should().Be("headlines");
    }

    [Test]
    public async Task FilesParameterShowsFiles()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var result = await handler.ExecuteAsync(documents, "files", tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Be(filesTree);
        result.Metadata.Extra["verbosity"].Should().Be("files");
        result.Metadata.Warning.Should().BeNull();
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task FoldersParameterShowsOnlyFolders()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var result = await handler.ExecuteAsync(documents, "folders", tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Be(foldersTree);
        result.Metadata.Extra["verbosity"].Should().Be("folders");
        result.Metadata.Warning.Should().BeNull();
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task HeadlinesFallsBackToFilesWithWarning()
    {
        var headline = string.Join(' ', Enumerable.Repeat("Alpha", 20));
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | " + headline;
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, headline, null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var filesTokens = TokenEstimator.EstimateTokens(filesTree);
        var headlinesTokens = TokenEstimator.EstimateTokens(headlinesTree);
        headlinesTokens.Should().BeGreaterThan(filesTokens);

        var result = await handler.ExecuteAsync(documents, "headlines", tokenBudget: headlinesTokens - 1, CancellationToken.None);

        result.Content.Should().Be(filesTree);
        result.Metadata.Extra["verbosity"].Should().Be("files");
        result.Metadata.Warning.Should().Contain("headlines");
        result.Metadata.Warning.Should().Contain("higher budget");
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task FilesFallsBackToFoldersWithWarning()
    {
        var headlinesTree = "file:///\n└── src/\n    ├── a.cs | Alpha\n    ├── b.cs | Beta\n    └── c.cs | Gamma";
        var filesTree = "file:///\n└── src/\n    ├── a.cs\n    ├── b.cs\n    └── c.cs";
        var foldersTree = "file:///\n└── src/ (3 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null),
            new ReadDocument("file:///src/b.cs", null, null, "Beta", null, null),
            new ReadDocument("file:///src/c.cs", null, null, "Gamma", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var filesTokens = TokenEstimator.EstimateTokens(filesTree);
        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        filesTokens.Should().BeGreaterThan(foldersTokens);

        var result = await handler.ExecuteAsync(documents, "files", tokenBudget: foldersTokens, CancellationToken.None);

        result.Content.Should().Be(foldersTree);
        result.Metadata.Extra["verbosity"].Should().Be("folders");
        result.Metadata.Warning.Should().Contain("files");
        result.Metadata.Warning.Should().Contain("higher budget");
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task HeadlinesFallsBackToFoldersWithWarning()
    {
        var headlinesTree = "file:///\n└── src/\n    ├── a.cs | Alpha\n    ├── b.cs | Beta\n    └── c.cs | Gamma";
        var filesTree = "file:///\n└── src/\n    ├── a.cs\n    ├── b.cs\n    └── c.cs";
        var foldersTree = "file:///\n└── src/ (3 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null),
            new ReadDocument("file:///src/b.cs", null, null, "Beta", null, null),
            new ReadDocument("file:///src/c.cs", null, null, "Gamma", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);

        var result = await handler.ExecuteAsync(documents, "headlines", tokenBudget: foldersTokens, CancellationToken.None);

        result.Content.Should().Be(foldersTree);
        result.Metadata.Extra["verbosity"].Should().Be("folders");
        result.Metadata.Warning.Should().Contain("headlines");
        result.Metadata.Warning.Should().Contain("higher budget");
        result.ExceedsBudget.Should().BeFalse();
    }

    [Test]
    public async Task MarksExceedsBudgetWhenFoldersTooLarge()
    {
        var headlinesTree = "file:///\n└── src/\n    ├── a.cs | Alpha\n    ├── b.cs | Beta\n    └── c.cs | Gamma";
        var filesTree = "file:///\n└── src/\n    ├── a.cs\n    ├── b.cs\n    └── c.cs";
        var foldersTree = "file:///\n└── src/ (3 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null),
            new ReadDocument("file:///src/b.cs", null, null, "Beta", null, null),
            new ReadDocument("file:///src/c.cs", null, null, "Gamma", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);
        var result = await handler.ExecuteAsync(documents, null, tokenBudget: foldersTokens - 1, CancellationToken.None);

        result.Content.Should().Be(foldersTree);
        result.ExceedsBudget.Should().BeTrue();
    }

    [Test]
    public async Task InvalidParameterThrowsArgumentException()
    {
        var handler = new TreeHandler(new StubContentProvider("", "", ""));
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };

        var action = async () => await handler.ExecuteAsync(documents, "invalid", tokenBudget: 1000, CancellationToken.None);

        await action.Should().ThrowExactlyAsync<ArgumentException>()
            .Where(e => e.Message.Contains("folders") && e.Message.Contains("files") && e.Message.Contains("headlines"));
    }

    [Test]
    public async Task FoldersParameterDoesNotShowFilesEvenWithBudget()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        // Even with a huge budget, folders parameter should only show folders
        var result = await handler.ExecuteAsync(documents, "folders", tokenBudget: 100000, CancellationToken.None);

        result.Content.Should().Be(foldersTree);
        result.Metadata.Extra["verbosity"].Should().Be("folders");
    }

    [Test]
    public async Task FilesParameterDoesNotShowHeadlinesEvenWithBudget()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, "Alpha", null, null)
        };
        var handler = new TreeHandler(new StubContentProvider(headlinesTree, filesTree, foldersTree));

        // Even with a huge budget, files parameter should only show files (not headlines)
        var result = await handler.ExecuteAsync(documents, "files", tokenBudget: 100000, CancellationToken.None);

        result.Content.Should().Be(filesTree);
        result.Metadata.Extra["verbosity"].Should().Be("files");
    }

    private sealed class StubContentProvider : IReadContentProvider
    {
        private readonly string _headlinesTree;
        private readonly string _filesTree;
        private readonly string _foldersTree;

        public StubContentProvider(string headlinesTree, string filesTree, string foldersTree)
        {
            _headlinesTree = headlinesTree;
            _filesTree = filesTree;
            _foldersTree = foldersTree;
        }

        public Task<ReadDocument?> FetchDocumentAsync(string uri, CancellationToken cancellationToken)
            => Task.FromResult<ReadDocument?>(null);

        public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string globUri, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReadDocument>>([]);

        public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, bool includeHeadlines, CancellationToken cancellationToken)
            => Task.FromResult<string?>(foldersOnly ? _foldersTree : (includeHeadlines ? _headlinesTree : _filesTree));
    }
}

internal sealed class FitToBudgetTests
{
    [Test]
    public async Task HeadlinesFitReturnHeadlines()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var provider = new StubContentProvider(headlinesTree, filesTree, foldersTree);
        var uris = new[] { "file:///src/a.cs" };

        var result = await TreeHandler.FitToBudgetAsync(
            provider, uris, tokenBudget: 10_000, TreeHandler.TreeDetailLevel.Headlines, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Content.Should().Be(headlinesTree);
        result.Verbosity.Should().Be(TreeHandler.TreeDetailLevel.Headlines);
    }

    [Test]
    public async Task HeadlinesExceedFallsBackToFiles()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | " + string.Join(' ', Enumerable.Repeat("Alpha", 20));
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var provider = new StubContentProvider(headlinesTree, filesTree, foldersTree);
        var uris = new[] { "file:///src/a.cs" };

        var headlineTokens = TokenEstimator.EstimateTokens(headlinesTree);
        var filesTokens = TokenEstimator.EstimateTokens(filesTree);
        // Budget fits files but not headlines
        var budget = headlineTokens - 1;
        budget.Should().BeGreaterThanOrEqualTo(filesTokens);

        var result = await TreeHandler.FitToBudgetAsync(
            provider, uris, tokenBudget: budget, TreeHandler.TreeDetailLevel.Headlines, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Content.Should().Be(filesTree);
        result.Verbosity.Should().Be(TreeHandler.TreeDetailLevel.Files);
    }

    [Test]
    public async Task FilesExceedFallsBackToFolders()
    {
        var headlinesTree = "file:///\n└── src/\n    ├── a.cs | Alpha\n    ├── b.cs | Beta\n    └── c.cs | Gamma";
        var filesTree = "file:///\n└── src/\n    ├── a.cs\n    ├── b.cs\n    └── c.cs";
        var foldersTree = "file:///\n└── src/ (3 cs)";
        var provider = new StubContentProvider(headlinesTree, filesTree, foldersTree);
        var uris = new[] { "file:///src/a.cs", "file:///src/b.cs", "file:///src/c.cs" };

        var foldersTokens = TokenEstimator.EstimateTokens(foldersTree);

        var result = await TreeHandler.FitToBudgetAsync(
            provider, uris, tokenBudget: foldersTokens, TreeHandler.TreeDetailLevel.Headlines, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Content.Should().Be(foldersTree);
        result.Verbosity.Should().Be(TreeHandler.TreeDetailLevel.Folders);
    }

    [Test]
    public async Task EverythingExceedsReturnsNull()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var provider = new StubContentProvider(headlinesTree, filesTree, foldersTree);
        var uris = new[] { "file:///src/a.cs" };

        var result = await TreeHandler.FitToBudgetAsync(
            provider, uris, tokenBudget: 1, TreeHandler.TreeDetailLevel.Headlines, CancellationToken.None);

        result.Should().BeNull();
    }

    [Test]
    public async Task MaxLevelFilesNeverTriesHeadlines()
    {
        var headlinesTree = "file:///\n└── src/\n    └── a.cs | Alpha";
        var filesTree = "file:///\n└── src/\n    └── a.cs";
        var foldersTree = "file:///\n└── src/ (1 cs)";
        var provider = new StubContentProvider(headlinesTree, filesTree, foldersTree);
        var uris = new[] { "file:///src/a.cs" };

        // Budget is huge — would fit headlines — but maxLevel is Files
        var result = await TreeHandler.FitToBudgetAsync(
            provider, uris, tokenBudget: 100_000, TreeHandler.TreeDetailLevel.Files, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Content.Should().Be(filesTree);
        result.Verbosity.Should().Be(TreeHandler.TreeDetailLevel.Files);
    }

    [Test]
    public async Task EmptyUrisReturnsNull()
    {
        var provider = new StubContentProvider("", "", "");
        var uris = Array.Empty<string>();

        var result = await TreeHandler.FitToBudgetAsync(
            provider, uris, tokenBudget: 10_000, TreeHandler.TreeDetailLevel.Headlines, CancellationToken.None);

        result.Should().BeNull();
    }

    private sealed class StubContentProvider : IReadContentProvider
    {
        private readonly string _headlinesTree;
        private readonly string _filesTree;
        private readonly string _foldersTree;

        public StubContentProvider(string headlinesTree, string filesTree, string foldersTree)
        {
            _headlinesTree = headlinesTree;
            _filesTree = filesTree;
            _foldersTree = foldersTree;
        }

        public Task<ReadDocument?> FetchDocumentAsync(string uri, CancellationToken cancellationToken)
            => Task.FromResult<ReadDocument?>(null);

        public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string globUri, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ReadDocument>>([]);

        public Task<string?> FormatAsTreeAsync(IReadOnlyList<string> uris, bool foldersOnly, bool includeHeadlines, CancellationToken cancellationToken)
            => Task.FromResult<string?>(foldersOnly ? _foldersTree : (includeHeadlines ? _headlinesTree : _filesTree));
    }
}
