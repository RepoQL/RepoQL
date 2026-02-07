using AwesomeAssertions;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class NoMatchDiagnosticsTests
{
    private static readonly IndexerStatus IdleStatus = new(IndexPending: 0, SemanticReady: true, SemanticEnabled: true, ElapsedMs: 0);
    private static readonly IndexerStatus PendingStatus = new(IndexPending: 12, SemanticReady: true, SemanticEnabled: true, ElapsedMs: 0);

    [Test]
    public async Task FileNotFound_NoFragment_NoGlob_ReturnsFileNotFound()
    {
        var provider = new SelectiveContentProvider();
        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/NonExistent.cs", provider, IdleStatus, CancellationToken.None);

        result.Should().Contain("File not found");
        result.Should().Contain("file:///src/NonExistent.cs");
        result.Should().Contain("tree: folders");
    }

    [Test]
    public async Task GlobMatchedNothing_ReturnsPatternMessage()
    {
        var provider = new SelectiveContentProvider();
        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/**/*.xyz", provider, IdleStatus, CancellationToken.None);

        result.Should().Contain("No files matched pattern");
        result.Should().Contain("*.xyz");
        result.Should().Contain("tree: folders");
    }

    [Test]
    public async Task SymbolNotFound_FileExists_ReturnsSymbolMessage()
    {
        var provider = new SelectiveContentProvider(
            ("file:///src/Auth.cs", new ReadDocument("file:///src/Auth.cs", "code", "text/plain", "Auth", null, null)));

        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/Auth.cs#symbol=NonExistent", provider, IdleStatus, CancellationToken.None);

        result.Should().Contain("File exists but no symbols matched");
        result.Should().Contain("NonExistent");
        result.Should().Contain("#symbol=*");
        result.Should().Contain("structure");
    }

    [Test]
    public async Task LineOutOfBounds_FileExists_ReturnsLineMessage()
    {
        var provider = new SelectiveContentProvider(
            ("file:///src/Auth.cs", new ReadDocument("file:///src/Auth.cs", "code", "text/plain", "Auth", null, null)));

        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/Auth.cs#line=9999", provider, IdleStatus, CancellationToken.None);

        result.Should().Contain("File exists but line range");
        result.Should().Contain("line=9999");
    }

    [Test]
    public async Task UnknownFragment_FileExists_ReturnsGenericFragmentMessage()
    {
        var provider = new SelectiveContentProvider(
            ("file:///src/Auth.cs", new ReadDocument("file:///src/Auth.cs", "code", "text/plain", "Auth", null, null)));

        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/Auth.cs#char=99999", provider, IdleStatus, CancellationToken.None);

        result.Should().Contain("File exists but fragment");
        result.Should().Contain("#char=99999");
    }

    [Test]
    public async Task IndexPending_MentionsPendingFiles()
    {
        var provider = new SelectiveContentProvider();
        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/NewFile.cs", provider, PendingStatus, CancellationToken.None);

        result.Should().Contain("12 files pending indexing");
        result.Should().Contain("not be indexed yet");
    }

    [Test]
    public async Task IndexPending_WithGlob_MentionsPendingFiles()
    {
        var provider = new SelectiveContentProvider();
        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/**/*.new", provider, PendingStatus, CancellationToken.None);

        result.Should().Contain("12 files pending indexing");
        result.Should().Contain("pattern");
    }

    [Test]
    public async Task MultiPattern_Semicolons_ReturnsGenericMessage()
    {
        var provider = new SelectiveContentProvider();
        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///a.cs;file:///b.cs", provider, IdleStatus, CancellationToken.None);

        result.Should().Contain("No files matched");
        result.Should().Contain("tree: folders");
    }

    [Test]
    public async Task SymbolNotFound_FileAlsoNotFound_ReturnsFileNotFound()
    {
        // When #symbol= fragment is present but even the base file doesn't exist
        var provider = new SelectiveContentProvider();
        var result = await NoMatchDiagnostics.DiagnoseAsync(
            "file:///src/Missing.cs#symbol=Foo", provider, IdleStatus, CancellationToken.None);

        // Should NOT say "File exists but..." — should say file not found
        result.Should().Contain("File not found");
        result.Should().NotContain("File exists");
    }

    /// <summary>
    /// Content provider that returns documents only for explicitly registered URIs.
    /// All other patterns return empty.
    /// </summary>
    private sealed class SelectiveContentProvider : IReadContentProvider
    {
        private readonly Dictionary<string, ReadDocument> _documents = new(StringComparer.OrdinalIgnoreCase);

        public SelectiveContentProvider(params (string uri, ReadDocument doc)[] entries)
        {
            foreach (var (uri, doc) in entries)
                _documents[uri] = doc;
        }

        public Task<IReadOnlyList<ReadDocument>> FetchGlobAsync(string uriPattern, CancellationToken cancellationToken)
        {
            if (_documents.TryGetValue(uriPattern, out var doc))
                return Task.FromResult<IReadOnlyList<ReadDocument>>([doc]);

            return Task.FromResult<IReadOnlyList<ReadDocument>>([]);
        }
    }
}
