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
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
        public Task<float[]?[]> EmbedBatchAsync(IReadOnlyList<string>? texts, CancellationToken ct = default)
            => Task.FromResult(texts?.Select(_ => (float[]?)null).ToArray() ?? []);
    }

    private class TestLlmProvider : ILlmProvider
    {
        public bool Enabled => false;
        public string Model => "disabled";
        public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, CancellationToken ct = default)
            => Task.FromResult("LLM disabled");
        public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, CancellationToken ct = default)
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
        var results = _db.Read("SELECT tree('[]')", r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().BeEmpty();
    }

    [Test]
    [DisplayName("tree with null/whitespace input returns empty string")]
    public void Tree_NullInput_ReturnsEmpty()
    {
        var results = _db.Read("SELECT tree('')", r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        results[0].Should().BeEmpty();
    }

    [Test]
    [DisplayName("tree with single file renders scheme and file")]
    public void Tree_SingleFile_RendersCorrectly()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/test.cs"]')""",
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
            """SELECT tree('["file:///a.cs", "docs:///readme.md"]')""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("docs:///");
        tree.Should().Contain("file:///");
    }

    [Test]
    [DisplayName("tree shows file counts for folders")]
    public void Tree_FolderCounts_Displayed()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/a.cs", "file:///src/b.cs", "file:///src/c.cs"]')""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("(3 files)");
    }

    [Test]
    [DisplayName("tree renders nested folders correctly")]
    public void Tree_NestedFolders_RendersCorrectly()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/Models/User.cs", "file:///src/Models/Order.cs", "file:///src/Services/UserService.cs"]')""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("Models/");
        tree.Should().Contain("Services/");
        tree.Should().Contain("(2 files)"); // Models has 2 files
        tree.Should().Contain("(1 file)");  // Services has 1 file
    }

    [Test]
    [DisplayName("tree uses correct box-drawing characters")]
    public void Tree_BoxDrawingCharacters_Used()
    {
        var results = _db.Read(
            """SELECT tree('["file:///a.cs", "file:///b.cs"]')""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        // Should have branch characters
        tree.Should().Contain("├── ");
        tree.Should().Contain("└── ");
    }

    [Test]
    [DisplayName("tree orders folders before files")]
    public void Tree_FoldersBeforeFiles()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/file.cs", "file:///src/subdir/nested.cs"]')""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;

        // subdir should appear before file.cs in the output
        var subdirIndex = tree.IndexOf("subdir/", StringComparison.Ordinal);
        var fileIndex = tree.IndexOf("file.cs", StringComparison.Ordinal);
        subdirIndex.Should().BeLessThan(fileIndex, "folders should be listed before files");
    }

    [Test]
    [DisplayName("tree handles github:// scheme")]
    public void Tree_GithubScheme_Works()
    {
        var results = _db.Read(
            """SELECT tree('["github://owner/repo/src/main.go"]')""",
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
            """SELECT tree(['file:///a.cs', 'file:///b.cs'])""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("file:///");
        tree.Should().Contain("a.cs");
        tree.Should().Contain("b.cs");
    }

    [Test]
    [DisplayName("tree singular file count for single file in folder")]
    public void Tree_SingleFileCount_UsesSingular()
    {
        var results = _db.Read(
            """SELECT tree('["file:///src/only.cs"]')""",
            r => r.IsDBNull(0) ? null : r.GetString(0));

        results.Should().HaveCount(1);
        var tree = results[0]!;
        tree.Should().Contain("(1 file)");
        tree.Should().NotContain("(1 files)");
    }
}
