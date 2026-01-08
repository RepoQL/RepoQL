using RepoQL.Data.DuckDB;
using AwesomeAssertions;
using RepoQL.Contracts;

namespace RepoQL.Tests;

internal class RepoUriGlobMatcherTests
{
    [Test]
    public void MatchesSingleSegmentWildcard()
    {
        var uri = "file:///repo/src/App.cs";
        var pattern = "file:///repo/src/*.cs";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void MatchesDoubleStarAcrossDirectories()
    {
        var uri = "file:///repo/src/app/components/Button.tsx";
        var pattern = "src/**/*.tsx";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void RespectsCaseSensitivityToggle()
    {
        var uri = "file:///repo/DOCS/README.md";
        var pattern = "file:///repo/docs/README.md";

        RepoUriGlobMatcher.IsMatch(uri, pattern, ignoreCase: true).Should().BeTrue();
        RepoUriGlobMatcher.IsMatch(uri, pattern, ignoreCase: false).Should().BeFalse();
    }

    [Test]
    public void HonorsExplicitScheme()
    {
        var uri = "embed:///repo/graphs/model.json";
        var pattern = "embed:///repo/graphs/*.json";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void ReturnsNullForBlankInputs()
    {
        RepoUriGlobMatcher.IsMatch(null, "src/**/*.md").Should().BeNull();
        RepoUriGlobMatcher.IsMatch("file:///repo/src/app.py", "  ").Should().BeNull();
    }

    [Test]
    public void SupportsCharacterClassRanges()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/src/bar.cs", "src/b[a-c]r.cs").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/src/bzr.cs", "src/b[a-c]r.cs").Should().BeFalse();
    }

    [Test]
    public void SupportsNegatedCharacterClasses()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/src/var.cs", "src/[!b]ar.cs").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/src/bar.cs", "src/[!b]ar.cs").Should().BeFalse();
    }

    [Test]
    [Skip("Glob escaping not yet implemented")]
    public void EscapedWildcardTreatedLiterally()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/docs/notes*.md", @"docs/notes\*.md").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/docs/notestar.md", @"docs/notes\*.md").Should().BeFalse();
    }

    [Test]
    public void LeadingSlashAnchorsToRepositoryRoot()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/src/main.cs", "/src/*.cs").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/app/src/main.cs", "/src/*.cs").Should().BeFalse();
    }

    [Test]
    public void DoubleStarMatchesDirectoriesAndFiles()
    {
        RepoUriGlobMatcher.IsMatch("file:///repo/assets/index.js", "assets/**/index.js").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/assets/lib/index.js", "assets/**/index.js").Should().BeTrue();
        RepoUriGlobMatcher.IsMatch("file:///repo/assets/lib/util.js", "assets/**/index.js").Should().BeFalse();
    }

    // === Fragment Matching: Symbol Patterns ===

    [Test]
    public void SymbolFragment_ExactMatch()
    {
        var uri = "file:///repo/src/App.cs#symbol=MyClass";
        var pattern = "src/App.cs#symbol=MyClass";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void SymbolFragment_DirectChildren_MatchesMethod()
    {
        var uri = "file:///repo/src/App.cs#symbol=MyClass.DoSomething";
        var pattern = "src/App.cs#symbol=MyClass.*";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void SymbolFragment_DirectChildren_DoesNotMatchNested()
    {
        var uri = "file:///repo/src/App.cs#symbol=MyClass.Inner.Method";
        var pattern = "src/App.cs#symbol=MyClass.*";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeFalse();
    }

    [Test]
    public void SymbolFragment_AllDescendants_MatchesNested()
    {
        var uri = "file:///repo/src/App.cs#symbol=MyClass.Inner.Method";
        var pattern = "src/App.cs#symbol=MyClass.**";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void SymbolFragment_AllDescendants_MatchesDirect()
    {
        var uri = "file:///repo/src/App.cs#symbol=MyClass.Method";
        var pattern = "src/App.cs#symbol=MyClass.**";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void SymbolFragment_CaseInsensitive()
    {
        var uri = "file:///repo/src/App.cs#symbol=MYCLASS.METHOD";
        var pattern = "src/App.cs#symbol=myclass.*";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void SymbolFragment_ContainerGlobWithFragment()
    {
        var uri = "file:///repo/src/services/UserService.cs#symbol=UserService.GetUser";
        var pattern = "src/**/*.cs#symbol=UserService.*";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    // === Fragment Matching: Line Patterns ===

    [Test]
    public void LineFragment_ExactRange()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#line=10,20";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void LineFragment_ExactRange_NoMatch()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#line=10,25";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeFalse();
    }

    [Test]
    public void LineFragment_ContainsLine()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#line=15";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void LineFragment_ContainsLine_NotInRange()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#line=25";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeFalse();
    }

    [Test]
    public void LineFragment_WildcardStart()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#line=*,20";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void LineFragment_WildcardEnd()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#line=10,*";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void LineFragment_FullWildcard()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#line=*";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    // === Fragment Matching: Edge Cases ===

    [Test]
    public void PatternWithFragment_NoUriFragment_NoMatch()
    {
        var uri = "file:///repo/src/App.cs";
        var pattern = "src/App.cs#symbol=MyClass";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeFalse();
    }

    [Test]
    public void PatternWithoutFragment_UriWithFragment_Matches()
    {
        // Pattern without fragment should match URI with any fragment
        var uri = "file:///repo/src/App.cs#symbol=MyClass";
        var pattern = "src/App.cs";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void MismatchedFragmentTypes_NoMatch()
    {
        var uri = "file:///repo/src/App.cs#line=10,20";
        var pattern = "src/App.cs#symbol=MyClass";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeFalse();
    }

    [Test]
    public void PlainAnchor_ExactMatch()
    {
        var uri = "file:///repo/docs/readme.md#section-1";
        var pattern = "docs/readme.md#section-1";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeTrue();
    }

    [Test]
    public void PlainAnchor_NoMatch()
    {
        var uri = "file:///repo/docs/readme.md#section-1";
        var pattern = "docs/readme.md#section-2";

        RepoUriGlobMatcher.IsMatch(uri, pattern).Should().BeFalse();
    }
}
