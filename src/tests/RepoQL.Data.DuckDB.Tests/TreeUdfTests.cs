using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;

namespace RepoQL.Data.DuckDB.Tests;

public class TreeUdfTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _db;

    public TreeUdfTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmbeddingProvider>(new TestEmbeddingProvider());
        services.AddSingleton<ILlmProvider>(new TestLlmProvider());
        services.AddSingleton<IMcpToolCaller?>(_ => null);
        services.AddSingleton<UriRegistry>();
        _serviceProvider = services.BuildServiceProvider();
        _db = new DuckDbDataStore(serviceProvider: _serviceProvider);
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
    }

    private class TestEmbeddingProvider : IEmbeddingProvider
    {
        public bool Enabled => false;
        public string Model => "disabled";
        public int Dimension => 384;
        public Task<float[]?> EmbedQueryAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?> EmbedPassageAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?[]> EmbedQueryBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
        public Task<float[]?[]> EmbedPassageBatchAsync(IReadOnlyList<string>? texts, BatchEmbeddingProgress progress, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private class TestLlmProvider : ILlmProvider
    {
        public bool Enabled => false;
        public string Model => "disabled";
        public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult("LLM disabled");
        public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
            => Task.FromResult(new LlmSummaryResult("LLM disabled"));
        public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
            => Task.FromResult("LLM disabled");
        public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
            => Task.FromResult(string.Empty);
    }

    [Test]
    [DisplayName("tree with empty array returns empty string")]
    public void Tree_EmptyArray_ReturnsEmpty()
    {
        var results = _db.Read("SELECT tree('[]', '[]', false)", r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().BeEmpty();
    }

    [Test]
    [DisplayName("tree with null/whitespace input returns empty string")]
    public void Tree_NullInput_ReturnsEmpty()
    {
        var results = _db.Read("SELECT tree('', '[]', false)", r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().BeEmpty();
    }

    [Test]
    [DisplayName("tree with single file renders scheme and file")]
    public void Tree_SingleFile_RendersCorrectly()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/test.cs"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("file:///");
        tree.Should().Contain("src/");
        tree.Should().Contain("test.cs");
    }

    [Test]
    [DisplayName("tree groups multiple schemes separately")]
    public void Tree_MultipleSchemes_GroupsCorrectly()
    {
        var results = _db.Read(
            """SELECT tree('["file:///a.cs", "help:///readme.md"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("help:///");
        tree.Should().Contain("file:///");
    }

    [Test]
    [DisplayName("tree shows file counts for folders")]
    public void Tree_FolderCounts_Displayed()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/a.cs", "file:///src/b.cs", "file:///src/c.cs"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("(3 cs)");
    }

    [Test]
    [DisplayName("tree renders nested folders correctly")]
    public void Tree_NestedFolders_RendersCorrectly()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/Models/User.cs", "file:///src/Models/Order.cs", "file:///src/Services/UserService.cs"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("Models/");
        tree.Should().Contain("Services/");
        tree.Should().Contain("(2 cs)"); // Models has 2 cs files
        tree.Should().Contain("(1 cs)"); // Services has 1 cs file
    }

    [Test]
    [DisplayName("tree uses correct box-drawing characters for folders")]
    public void Tree_BoxDrawingCharacters_Used()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/a.cs", "file:///lib/b.cs"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        // Should have branch characters for folders (files don't get branch chars)
        tree.Should().Contain("├── "); // First folder (lib/ alphabetically, but src/ comes after)
        tree.Should().Contain("└── "); // Last folder
    }

    [Test]
    [DisplayName("tree orders files before folders")]
    public void Tree_FilesBeforeFolders()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/file.cs", "file:///src/subdir/nested.cs"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;

        // file.cs should appear before subdir in the output (files first, then folders)
        var fileIndex = tree.IndexOf("file.cs", StringComparison.Ordinal);
        var subdirIndex = tree.IndexOf("subdir/", StringComparison.Ordinal);
        fileIndex.Should().BeLessThan(subdirIndex, "files should be listed before folders");
    }

    [Test]
    [DisplayName("tree handles github:// scheme")]
    public void Tree_GithubScheme_Works()
    {
        var results = _db.Read(
            """SELECT tree('["github://owner/repo/src/main.go"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("github://");
    }

    [Test]
    [DisplayName("tree works with DuckDB array syntax")]
    public void Tree_ArraySyntax_Works()
    {
        var results = _db.Read(
            """SELECT tree(['file:///a.cs', 'file:///b.cs'], '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("file:///");
        tree.Should().Contain("a.cs");
        tree.Should().Contain("b.cs");
    }

    [Test]
    [DisplayName("tree shows type count for single file in folder")]
    public void Tree_SingleFileCount_ShowsType()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/only.cs"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("(1 cs)");
    }

    [Test]
    [DisplayName("tree with foldersOnly=true hides files and shows type counts")]
    public void Tree_FoldersOnly_HidesFilesAndShowsTypeCounts()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/a.cs", "file:///src/b.cs", "file:///src/config.json", "file:///lib/helper.cs"]', '[]', true)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;

        // Should NOT contain individual file names
        tree.Should().NotContain("a.cs");
        tree.Should().NotContain("b.cs");
        tree.Should().NotContain("config.json");
        tree.Should().NotContain("helper.cs");

        // Should contain folder with extension counts
        tree.Should().Contain("src/");
        tree.Should().Contain("lib/");
        tree.Should().Contain("2 cs"); // src/ has 2 cs files
        tree.Should().Contain("1 json"); // src/ has 1 json file
    }

    [Test]
    [DisplayName("tree with foldersOnly=false shows files (default behavior)")]
    public void Tree_FoldersOnlyFalse_ShowsFiles()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/a.cs", "file:///src/b.cs"]', '[]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;

        // Should contain file names
        tree.Should().Contain("a.cs");
        tree.Should().Contain("b.cs");
    }

    [Test]
    [DisplayName("tree uses headline as display name when provided")]
    public void Tree_Headlines_WhenProvided()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/a.cs"]', '["a.cs | Alpha class | 50 ln"]', false)""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        // Headline replaces filename entirely
        tree.Should().Contain("a.cs | Alpha class | 50 ln");
    }
}
