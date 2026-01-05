using AwesomeAssertions;
using RepoQL.Contracts;

namespace RepoQL.Tests;

internal class UriPatternMatcherTests
{
    // === Single Pattern ===

    [Test]
    public void SingleUri_ExactMatch()
    {
        var result = UriPatternMatcher.Matches("file:///docs/readme.md", "file:///docs/readme.md");
        result.Should().BeTrue();
    }

    [Test]
    public void SingleUri_NoMatch()
    {
        var result = UriPatternMatcher.Matches("file:///docs/other.md", "file:///docs/readme.md");
        result.Should().BeFalse();
    }

    [Test]
    public void SingleGlob_MatchesSingleFile()
    {
        // Pattern matches anywhere in the path, so URI needs a prefix before the pattern
        var result = UriPatternMatcher.Matches("file:///repo/src/App.cs", "src/**/*.cs");
        result.Should().BeTrue();
    }

    [Test]
    public void SingleGlob_MatchesNestedFile()
    {
        var result = UriPatternMatcher.Matches("file:///repo/src/sub/deep/Bar.cs", "src/**/*.cs");
        result.Should().BeTrue();
    }

    [Test]
    public void SingleGlob_DoesNotMatchWrongExtension()
    {
        var result = UriPatternMatcher.Matches("file:///repo/src/file.txt", "src/**/*.cs");
        result.Should().BeFalse();
    }

    // === Multiple Patterns (OR) ===

    [Test]
    public void SemicolonDelimited_MatchesFirstPattern()
    {
        var result = UriPatternMatcher.Matches("file:///a.md", "a.md;b.md");
        result.Should().BeTrue();
    }

    [Test]
    public void SemicolonDelimited_MatchesSecondPattern()
    {
        var result = UriPatternMatcher.Matches("file:///b.md", "a.md;b.md");
        result.Should().BeTrue();
    }

    [Test]
    public void SemicolonDelimited_NoMatchForOther()
    {
        var result = UriPatternMatcher.Matches("file:///c.md", "a.md;b.md");
        result.Should().BeFalse();
    }

    [Test]
    public void MultipleGlobPatterns_MatchesEither()
    {
        var spec = "src/**/*.cs;lib/**/*.cs";
        UriPatternMatcher.Matches("file:///repo/src/App.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/lib/Helper.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/other/File.cs", spec).Should().BeFalse();
    }

    // === Negative Patterns ===

    [Test]
    public void NegativePattern_ExcludesMatch()
    {
        var spec = "src/**;!src/tests/**";
        UriPatternMatcher.Matches("file:///repo/src/App.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/src/tests/AppTests.cs", spec).Should().BeFalse();
    }

    [Test]
    public void NegativePattern_ExcludesSpecificExtension()
    {
        var spec = "src/**;!**/*.g.cs";
        UriPatternMatcher.Matches("file:///repo/src/App.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/src/Model.g.cs", spec).Should().BeFalse();
    }

    [Test]
    public void MultipleNegativePatterns_AllApply()
    {
        var spec = "src/**;!src/tests/**;!src/generated/**";
        UriPatternMatcher.Matches("file:///repo/src/App.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/src/tests/T.cs", spec).Should().BeFalse();
        UriPatternMatcher.Matches("file:///repo/src/generated/G.cs", spec).Should().BeFalse();
    }

    // === Only Negatives ===

    [Test]
    public void OnlyNegatives_MatchesEverythingExcept()
    {
        var spec = "!**/*.md";
        UriPatternMatcher.Matches("file:///repo/src/App.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/docs/readme.md", spec).Should().BeFalse();
    }

    [Test]
    public void OnlyNegatives_MultiplePatterns()
    {
        var spec = "!**/*.md;!**/*.txt";
        UriPatternMatcher.Matches("file:///repo/src/App.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/docs/readme.md", spec).Should().BeFalse();
        UriPatternMatcher.Matches("file:///repo/notes.txt", spec).Should().BeFalse();
    }

    // === Blank = Everything ===

    [Test]
    public void BlankSpec_MatchesEverything()
    {
        UriPatternMatcher.Matches("file:///anything", null).Should().BeTrue();
        UriPatternMatcher.Matches("file:///src/deep/path/file.cs", null).Should().BeTrue();
    }

    [Test]
    public void EmptyString_MatchesEverything()
    {
        UriPatternMatcher.Matches("file:///anything", "").Should().BeTrue();
    }

    [Test]
    public void WhitespaceOnly_MatchesEverything()
    {
        UriPatternMatcher.Matches("file:///anything", "   ").Should().BeTrue();
    }

    // === Scheme Inference ===

    [Test]
    public void ShorthandPath_InfersFileScheme()
    {
        var result = UriPatternMatcher.Matches("file:///src/App.cs", "src/*.cs");
        result.Should().BeTrue();
    }

    [Test]
    public void ShorthandPath_InfersFileSchemeForDeep()
    {
        var result = UriPatternMatcher.Matches("file:///docs/readme.md", "docs/**/*.md");
        result.Should().BeTrue();
    }

    // === Three-Valued Logic ===

    [Test]
    public void NullUri_ReturnsNull()
    {
        var result = UriPatternMatcher.Matches(null, "**/*.cs");
        result.Should().BeNull();
    }

    [Test]
    public void EmptyUri_ReturnsNull()
    {
        var result = UriPatternMatcher.Matches("", "**/*.cs");
        result.Should().BeNull();
    }

    [Test]
    public void WhitespaceUri_ReturnsNull()
    {
        var result = UriPatternMatcher.Matches("   ", "**/*.cs");
        result.Should().BeNull();
    }

    // === ParsePatterns ===

    [Test]
    public void ParsePatterns_SeparatesPositiveAndNegative()
    {
        var (pos, neg) = UriPatternMatcher.ParsePatterns("src/**;!src/tests/**;lib/**;!**/*.g.cs");
        pos.Should().BeEquivalentTo(["src/**", "lib/**"]);
        neg.Should().BeEquivalentTo(["src/tests/**", "**/*.g.cs"]);
    }

    [Test]
    public void ParsePatterns_HandlesOnlyNegatives()
    {
        var (pos, neg) = UriPatternMatcher.ParsePatterns("!**/*.md;!**/*.txt");
        pos.Should().BeEmpty();
        neg.Should().BeEquivalentTo(["**/*.md", "**/*.txt"]);
    }

    [Test]
    public void ParsePatterns_HandlesBlank()
    {
        var (pos, neg) = UriPatternMatcher.ParsePatterns(null);
        pos.Should().BeEmpty();
        neg.Should().BeEmpty();
    }

    // === Edge Cases ===

    [Test]
    public void TrailingSemicolon_Ignored()
    {
        var result = UriPatternMatcher.Matches("file:///a.md", "a.md;");
        result.Should().BeTrue();
    }

    [Test]
    public void LeadingSemicolon_Ignored()
    {
        var result = UriPatternMatcher.Matches("file:///a.md", ";a.md");
        result.Should().BeTrue();
    }

    [Test]
    public void EmptyNegative_Ignored()
    {
        var result = UriPatternMatcher.Matches("file:///a.md", "a.md;!");
        result.Should().BeTrue();
    }

    [Test]
    public void MultipleSemicolons_Ignored()
    {
        var result = UriPatternMatcher.Matches("file:///a.md", "a.md;;;b.md");
        result.Should().BeTrue();
    }

    [Test]
    public void WhitespaceAroundPatterns_Trimmed()
    {
        var result = UriPatternMatcher.Matches("file:///a.md", "  a.md  ;  b.md  ");
        result.Should().BeTrue();
    }

    // === Complex Scenarios ===

    [Test]
    public void ComplexFilter_MixedPositiveAndNegative()
    {
        var spec = "src/**;lib/**;!src/generated/**;!**/*.g.cs";

        UriPatternMatcher.Matches("file:///repo/src/App.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/lib/Helper.cs", spec).Should().BeTrue();
        UriPatternMatcher.Matches("file:///repo/src/generated/Model.cs", spec).Should().BeFalse();
        UriPatternMatcher.Matches("file:///repo/src/App.g.cs", spec).Should().BeFalse();
        UriPatternMatcher.Matches("file:///repo/other/file.cs", spec).Should().BeFalse();
    }

    [Test]
    public void DocsScheme_Supported()
    {
        var result = UriPatternMatcher.Matches("docs:///quickstart.md", "docs:///quickstart.md");
        result.Should().BeTrue();
    }

    [Test]
    public void CaseInsensitive_ByDefault()
    {
        var result = UriPatternMatcher.Matches("file:///SRC/APP.CS", "src/app.cs");
        result.Should().BeTrue();
    }

    [Test]
    public void CaseSensitive_WhenSpecified()
    {
        var result = UriPatternMatcher.Matches("file:///SRC/APP.CS", "src/app.cs", ignoreCase: false);
        result.Should().BeFalse();
    }
}
