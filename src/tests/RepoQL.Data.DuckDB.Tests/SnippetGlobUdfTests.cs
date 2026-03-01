using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.Contracts;

namespace RepoQL.Data.DuckDB.Tests;

public sealed class SnippetGlobUdfTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly ServiceProvider _serviceProvider;
    private readonly DuckDbDataStore _db;
    private readonly UriRegistry _registry;

    public SnippetGlobUdfTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "RepoQL", "SnippetGlobUdfTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);

        var services = new ServiceCollection();
        services.AddTestUdfDependencies(_repoRoot);
        _serviceProvider = services.BuildServiceProvider();

        _registry = _serviceProvider.GetRequiredService<UriRegistry>();
        _db = new DuckDbDataStore(serviceProvider: _serviceProvider);
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();

        if (!Directory.Exists(_repoRoot))
            return;

        try
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }

    [Test]
    public void SnippetGlob_ReturnsUriAndSnippet()
    {
        CreateTrackedFile("demo/a.cs", "class A {}");
        CreateTrackedFile("demo/b.cs", "class B {}");

        var rows = _db.Read(
            @"SELECT uri, snippet
              FROM snippet_glob('file:///demo/*.cs')
              ORDER BY uri",
            r => (Uri: r.GetString(0), Snippet: r.GetString(1)));

        rows.Should().HaveCount(2);
        rows[0].Uri.Should().Be("file:///demo/a.cs");
        rows[0].Snippet.Should().Be("class A {}");
        rows[1].Uri.Should().Be("file:///demo/b.cs");
        rows[1].Snippet.Should().Be("class B {}");
    }

    [Test]
    public void SnippetGlob_RespectsMaxResults()
    {
        CreateTrackedFile("limit/a.cs", "class A {}");
        CreateTrackedFile("limit/b.cs", "class B {}");

        var rows = _db.Read(
            @"SELECT uri, snippet
              FROM snippet_glob('file:///limit/*.cs', 1)
              ORDER BY uri",
            r => (Uri: r.GetString(0), Snippet: r.GetString(1)));

        rows.Should().HaveCount(1);
        rows[0].Uri.Should().Be("file:///limit/a.cs");
        rows[0].Snippet.Should().Be("class A {}");
    }

    [Test]
    public void SnippetGlob_SupportsLineRanges()
    {
        CreateTrackedFile("ranges/demo.cs", "line1\nline2\nline3\nline4");

        var rows = _db.Read(
            @"SELECT uri, snippet
              FROM snippet_glob('file:///ranges/demo.cs#line=2,3')",
            r => (Uri: r.GetString(0), Snippet: r.GetString(1)));

        rows.Should().HaveCount(1);
        rows[0].Uri.Should().Be("file:///ranges/demo.cs#line=2,3");
        rows[0].Snippet.Should().Be("line2\nline3");
    }

    [Test]
    public void SnippetGlob_SupportsSymbolAndExclusion()
    {
        CreateTrackedFile("symbols/demo.cs", "header\nfoo-start\nfoo-end\nbar-line\ntail");

        var fileUri = RepoUri.Parse("file:///symbols/demo.cs");
        var fooSymbolUri = RepoUri.Parse("file:///symbols/demo.cs#symbol=Demo.Foo");
        var barSymbolUri = RepoUri.Parse("file:///symbols/demo.cs#symbol=Demo.Bar");

        _registry.SetIndexed(
            fileUri,
            lineCount: 5,
            symbols: new Dictionary<RepoUri, SymbolEntry>
            {
                [fooSymbolUri] = new("csharp.method", 2, 3),
                [barSymbolUri] = new("csharp.method", 4, 4)
            },
            headline: null,
            structure: null);

        var rows = _db.Read(
            @"SELECT uri, snippet
              FROM snippet_glob('file:///symbols/demo.cs#symbol=Demo.*;!#symbol=Demo.Bar')
              ORDER BY uri",
            r => (Uri: r.GetString(0), Snippet: r.GetString(1)));

        rows.Should().HaveCount(1);
        rows[0].Uri.Should().Be("file:///symbols/demo.cs#symbol=Demo.Foo");
        rows[0].Snippet.Should().Be("foo-start\nfoo-end");
    }

    private void CreateTrackedFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);

        var uri = RepoUri.Parse("file:///" + relativePath.Replace('\\', '/'));
        _registry.TryRegisterDiscovered(uri);
    }
}
