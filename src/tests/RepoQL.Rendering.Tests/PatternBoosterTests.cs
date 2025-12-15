using System.Text.RegularExpressions;
using AwesomeAssertions;
using RepoQL.Xray.Search;

namespace RepoQL.Rendering.Tests;

/// <summary>
/// Tests for pattern boost feature in xray tool.
/// Verifies that regex patterns can boost and penalize search results.
/// </summary>
internal class PatternBoosterTests
{
    [Test]
    public void ApplyBoosts_WithSimplePattern_BoostsMatchingResults()
    {
        // Arrange
        var results = new List<SearchResult>
        {
            new(
                Uri: "file:///src/Enhanced.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Enhanced search engine",
                Structure: "Contains Enhanced pattern",
                Snippet: "public class Enhanced { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            ),
            new(
                Uri: "file:///src/Standard.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Standard search engine",
                Structure: "Regular implementation",
                Snippet: "public class Standard { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            )
        };

        var patterns = PatternBooster.ParsePatterns("Enhanced.*");

        // Act
        PatternBooster.ApplyBoosts(results, patterns);

        // Assert
        // Enhanced.cs should have higher score due to boost
        results[0].RawScore.Should().BeGreaterThan(results[1].RawScore,
            "Enhanced.cs should be boosted higher than Standard.cs");
    }

    [Test]
    public void ApplyBoosts_WithMultiplePatterns_BoostsCompound()
    {
        // Arrange
        var results = new List<SearchResult>
        {
            new(
                Uri: "file:///src/EnhancedXray.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Enhanced Xray search engine",
                Structure: "Contains both Enhanced and Xray",
                Snippet: "public class EnhancedXray { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            ),
            new(
                Uri: "file:///src/EnhancedSearcher.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Enhanced search implementation",
                Structure: "Contains Enhanced only",
                Snippet: "public class EnhancedSearcher { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            )
        };

        var patterns = PatternBooster.ParsePatterns("Enhanced.*,Xray.*");

        // Act
        PatternBooster.ApplyBoosts(results, patterns);

        // Assert
        // EnhancedXray.cs matches both patterns, so should be boosted more
        results[0].RawScore.Should().BeGreaterThan(results[1].RawScore,
            "EnhancedXray.cs matches 2 patterns and should rank higher than EnhancedSearcher.cs which matches 1 pattern");
    }

    [Test]
    public void ApplyBoosts_WithCaseInsensitivePattern_BoostsIgnoringCase()
    {
        // Arrange
        var results = new List<SearchResult>
        {
            new(
                Uri: "file:///src/Error.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Error handling",
                Structure: "Contains ERROR in uppercase",
                Snippet: "public class ErrorHandler { throw new ERROR(); }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            ),
            new(
                Uri: "file:///src/Standard.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Standard implementation",
                Structure: "No error handling",
                Snippet: "public class Standard { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            )
        };

        // Case-insensitive pattern
        var patterns = PatternBooster.ParsePatterns("(?i)error");

        // Act
        PatternBooster.ApplyBoosts(results, patterns);

        // Assert
        results[0].RawScore.Should().BeGreaterThan(results[1].RawScore,
            "Case-insensitive pattern should match 'ERROR' even though it's uppercase");
    }

    [Test]
    public void ApplyBoosts_WithWordBoundaryPattern_BoostsWordMatches()
    {
        // Arrange
        var results = new List<SearchResult>
        {
            new(
                Uri: "file:///src/Authentication.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Authentication service",
                Structure: "Contains Auth",
                Snippet: "public class AuthenticationService { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            ),
            new(
                Uri: "file:///src/Authorize.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Authorization module",
                Structure: "Contains Auth at word boundary",
                Snippet: "public class Authorize { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            )
        };

        // Word boundary pattern
        var patterns = PatternBooster.ParsePatterns(@"\bAuth\w*");

        // Act
        PatternBooster.ApplyBoosts(results, patterns);

        // Assert
        // Both should match as they both contain "Auth" at word boundary
        results[0].RawScore.Should().BeGreaterThan(100.0,
            "AuthenticationService should be boosted");
        results[1].RawScore.Should().BeGreaterThan(100.0,
            "Authorize should be boosted");
    }

    [Test]
    public void ParsePatterns_WithValidPatterns_ReturnsCompiledRegexes()
    {
        // Arrange
        var patternString = "Enhanced.*,Xray.*,(?i)test";

        // Act
        var patterns = PatternBooster.ParsePatterns(patternString);

        // Assert
        patterns.Should().HaveCount(3,
            "Should parse 3 comma-separated patterns");
        patterns.All(p => p is Regex).Should().BeTrue(
            "All items should be compiled Regex objects");
    }

    [Test]
    public void ParsePatterns_WithInvalidPatterns_SkipsInvalidOnes()
    {
        // Arrange
        // Mix of valid and invalid regex patterns
        // (?<name> is invalid - should be (?<name>...) or (?'name'...)
        var patternString = "Enhanced.*,(?<invalidname,Xray.*";

        // Act
        var patterns = PatternBooster.ParsePatterns(patternString);

        // Assert
        // Should skip the invalid patterns
        // With at least one valid pattern out of 3, we should get 1-2 patterns back
        patterns.Count.Should().BeLessThan(3,
            "Invalid regex patterns should be skipped");
        patterns.Count.Should().BeGreaterThanOrEqualTo(1,
            "At least one valid pattern should parse");
    }

    [Test]
    public void ParsePatterns_WithEmptyString_ReturnsEmpty()
    {
        // Act
        var patterns = PatternBooster.ParsePatterns("");

        // Assert
        patterns.Should().BeEmpty();
    }

    [Test]
    public void ParsePatterns_WithNull_ReturnsEmpty()
    {
        // Act
        var patterns = PatternBooster.ParsePatterns(null);

        // Assert
        patterns.Should().BeEmpty();
    }

    [Test]
    public void ApplyBoosts_WithoutMatches_DoesNotChangeScore()
    {
        // Arrange
        var results = new List<SearchResult>
        {
            new(
                Uri: "file:///src/Unrelated.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Unrelated module",
                Structure: "No matching keywords",
                Snippet: "public class Unrelated { }",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0,
                Confidence: 50,
                ChildObjects: null
            )
        };

        var patterns = PatternBooster.ParsePatterns("Enhanced.*,Xray.*");

        // Act
        PatternBooster.ApplyBoosts(results, patterns);

        // Assert
        results[0].RawScore.Should().Be(100.0,
            "Score should not change if no patterns match");
    }

    [Test]
    public void ApplyBoosts_BoostedScoresRankHigherInResults()
    {
        // Arrange: Create a list where boost should reorder items
        var results = new List<SearchResult>
        {
            new(
                Uri: "file:///src/Standard.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Standard search",
                Structure: "Initial high score",
                Snippet: "basic implementation",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 105.0, // Initially higher
                Confidence: 50,
                ChildObjects: null
            ),
            new(
                Uri: "file:///src/EnhancedSearch.cs",
                Scope: SearchScope.Document,
                Kind: null,
                Symbol: null,
                Headline: "Enhanced search implementation",
                Structure: "Lower initial score but boosted",
                Snippet: "advanced implementation",
                LineStart: null,
                LineEnd: null,
                Lang: "csharp",
                SemanticType: null,
                RawScore: 100.0, // Initially lower
                Confidence: 50,
                ChildObjects: null
            )
        };

        var patterns = PatternBooster.ParsePatterns("Enhanced.*");

        // Act
        PatternBooster.ApplyBoosts(results, patterns);

        // Assert
        // Even though Standard.cs started with higher score (105),
        // EnhancedSearch.cs with boost should now be higher
        results[1].RawScore.Should().BeGreaterThan(results[0].RawScore,
            "Boosted Enhanced result should rank higher than unrelated result");
    }
}
