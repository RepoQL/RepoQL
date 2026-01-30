using AwesomeAssertions;
using RepoQL.Contracts;

namespace RepoQL.Tests;

internal class UriRegistryTests
{
    // === Basic Operations ===

    [Test]
    public void TryRegisterDiscovered_NewFile_ReturnsTrue()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");

        var result = registry.TryRegisterDiscovered(uri);

        result.Should().BeTrue();
        registry.Should().ContainKey(uri);
        registry[uri].Status.Should().Be(UriStatus.Discovered);
    }

    [Test]
    public void TryRegisterDiscovered_ExistingFile_ReturnsFalse()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.TryRegisterDiscovered(uri);

        var result = registry.TryRegisterDiscovered(uri);

        result.Should().BeFalse();
    }

    [Test]
    public void SetIndexed_UpdatesStatusAndSymbols()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var symbolUri = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");
        registry.TryRegisterDiscovered(fileUri);

        var symbols = new Dictionary<RepoUri, string> { { symbolUri, "type" } };
        registry.SetIndexed(fileUri, symbols.AsReadOnly());

        registry[fileUri].Status.Should().Be(UriStatus.Indexed);
        registry[fileUri].Symbols.Should().ContainKey(symbolUri);
        registry[fileUri].IndexedAt.Should().NotBeNull();
    }

    [Test]
    public void SetFailed_SetsErrorMessage()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");

        registry.SetFailed(uri, "Parse error");

        registry[uri].Status.Should().Be(UriStatus.Failed);
        registry[uri].Error.Should().Be("Parse error");
    }

    [Test]
    public void SetEmbedded_UpdatesEmbeddingStatus()
    {
        var registry = new UriRegistry();
        var uri = RepoUri.Parse("file:///src/App.cs");
        registry.SetIndexed(uri, new Dictionary<RepoUri, string>().AsReadOnly());

        registry.SetEmbedded(uri, 5);

        registry[uri].EmbeddingStatus.Should().Be(EmbeddingStatus.Embedded);
        registry[uri].EmbeddedChunkCount.Should().Be(5);
        registry[uri].EmbeddedAt.Should().NotBeNull();
    }

    // === Pattern Matching ===

    [Test]
    public void MatchPattern_BlankPattern_ReturnsAllFiles()
    {
        var registry = CreateTestRegistry();

        var matches = registry.MatchPattern(null).ToList();

        matches.Should().HaveCount(3); // All 3 files
    }

    [Test]
    public void MatchPattern_SimpleGlob_MatchesFiles()
    {
        var registry = CreateTestRegistry();

        var matches = registry.MatchPattern("src/**/*.cs").ToList();

        matches.Should().HaveCount(2);
        matches.Should().Contain(m => m.AbsoluteUri.Contains("App.cs"));
        matches.Should().Contain(m => m.AbsoluteUri.Contains("Utils.cs"));
    }

    [Test]
    public void MatchPattern_WithExclusion_ExcludesMatches()
    {
        var registry = CreateTestRegistry();

        var matches = registry.MatchPattern("src/**/*.cs;!**/Utils.cs").ToList();

        matches.Should().HaveCount(1);
        matches.Should().Contain(m => m.AbsoluteUri.Contains("App.cs"));
    }

    [Test]
    public void MatchPattern_WithFragmentPattern_MatchesSymbols()
    {
        var registry = CreateTestRegistry();

        var matches = registry.MatchPattern("src/**/*.cs#symbol=MyClass*").ToList();

        matches.Should().HaveCount(2); // MyClass and MyClassHelper
        matches.Should().AllSatisfy(m => m.AbsoluteUri.Should().Contain("#symbol=MyClass"));
    }

    [Test]
    public void MatchPattern_SymbolWildcard_MatchesSuffix()
    {
        var registry = CreateTestRegistry();

        var matches = registry.MatchPattern("src/**/*.cs#symbol=*Helper").ToList();

        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Contain("MyClassHelper");
    }

    // === Scope Readiness ===

    [Test]
    public void CheckScope_AllReady_IsReadyTrue()
    {
        var registry = CreateTestRegistry();
        // Mark all as embedded
        foreach (var uri in registry.Keys)
        {
            registry.SetEmbedded(uri, 1);
        }

        var readiness = registry.CheckScope("src/**/*.cs");

        readiness.IsReady.Should().BeTrue();
        readiness.PendingIndex.Should().BeEmpty();
        readiness.PendingEmbedding.Should().BeEmpty();
    }

    [Test]
    public void CheckScope_SomePending_IsReadyFalse()
    {
        var registry = CreateTestRegistry();
        // Only embed one file
        var firstFile = registry.Keys.First();
        registry.SetEmbedded(firstFile, 1);

        var readiness = registry.CheckScope("src/**/*.cs");

        readiness.IsReady.Should().BeFalse();
        readiness.PendingEmbedding.Should().NotBeEmpty();
    }

    [Test]
    public void CheckScope_EmptyPattern_ReturnsAllFiles()
    {
        var registry = CreateTestRegistry();

        var readiness = registry.CheckScope(null);

        readiness.TotalFiles.Should().Be(3);
    }

    [Test]
    public void CheckScope_NoMatches_ReturnsEmpty()
    {
        var registry = CreateTestRegistry();

        var readiness = registry.CheckScope("nonexistent/**");

        readiness.TotalFiles.Should().Be(0);
        readiness.IsReady.Should().BeTrue(); // Empty scope is "ready"
    }

    [Test]
    public void CheckScope_Summary_DescribesState()
    {
        var registry = CreateTestRegistry();

        var readiness = registry.CheckScope("src/**/*.cs");

        readiness.Summary.Should().Contain("pending embedding");
    }

    // === Registry Summary ===

    [Test]
    public void GetSummary_CountsCorrectly()
    {
        var registry = CreateTestRegistry();

        var summary = registry.GetSummary();

        summary.TotalFiles.Should().Be(3);
        summary.TotalSymbols.Should().Be(3); // 2 in App.cs, 1 in Utils.cs
        summary.ByStatus[UriStatus.Indexed].Should().Be(3);
    }

    // === Helper ===

    private static UriRegistry CreateTestRegistry()
    {
        var registry = new UriRegistry();

        // File 1: App.cs with 2 symbols
        var appUri = RepoUri.Parse("file:///src/App.cs");
        var symbol1 = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");
        var symbol2 = RepoUri.Parse("file:///src/App.cs#symbol=MyClassHelper");
        registry.SetIndexed(appUri, new Dictionary<RepoUri, string>
        {
            { symbol1, "type" },
            { symbol2, "type" }
        }.AsReadOnly());

        // File 2: Utils.cs with 1 symbol
        var utilsUri = RepoUri.Parse("file:///src/Utils.cs");
        var symbol3 = RepoUri.Parse("file:///src/Utils.cs#symbol=StringUtils");
        registry.SetIndexed(utilsUri, new Dictionary<RepoUri, string>
        {
            { symbol3, "type" }
        }.AsReadOnly());

        // File 3: README.md with no symbols
        var readmeUri = RepoUri.Parse("file:///README.md");
        registry.SetIndexed(readmeUri, new Dictionary<RepoUri, string>().AsReadOnly());

        return registry;
    }
}
