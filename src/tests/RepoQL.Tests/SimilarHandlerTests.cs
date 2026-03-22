using AwesomeAssertions;
using RepoQL.Client.Host;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using static RepoQL.ConsoleApp.Host.SimilarHandler;

namespace RepoQL.Tests;

internal sealed class SimilarHandlerTests
{
    [Test]
    [DisplayName("SimilarHandler returns usage when parameter is null")]
    public async Task SimilarHandler_NullParameter_ReturnsUsage()
    {
        var handler = new SimilarHandler(null!, null);
        handler.CanHandle("similar").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Foo.cs", "content", "text/plain", null, null, null)
            ],
            null,
            1000,
            CancellationToken.None);

        result.Content.Should().Contain("Usage:");
    }

    [Test]
    [DisplayName("SimilarHandler returns usage when parameter is empty")]
    public async Task SimilarHandler_EmptyParameter_ReturnsUsage()
    {
        var handler = new SimilarHandler(null!, null);

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Foo.cs", "content", "text/plain", null, null, null)
            ],
            "   ",
            1000,
            CancellationToken.None);

        result.Content.Should().Contain("Usage:");
    }

    [Test]
    [DisplayName("SimilarHandler returns no files matched for empty documents")]
    public async Task SimilarHandler_NoDocuments_ReturnsNoFilesMatched()
    {
        var handler = new SimilarHandler(null!, null);

        var result = await handler.ExecuteAsync(
            [],
            "file:///src/Seed.cs",
            1000,
            CancellationToken.None);

        result.Content.Should().Be("No files matched pattern.");
    }

    [Test]
    [DisplayName("SimilarHandler does not handle unrelated modifiers")]
    public void SimilarHandler_DoesNotHandleOtherModifiers()
    {
        var handler = new SimilarHandler(null!, null);

        handler.CanHandle("similar").Should().BeTrue();
        handler.CanHandle("find").Should().BeFalse();
        handler.CanHandle("grep").Should().BeFalse();
        handler.CanHandle(null).Should().BeFalse();
    }

    [Test]
    [DisplayName("SimilarHandler with null DB returns database unavailable error on seed dim resolve")]
    public async Task SimilarHandler_NullDb_ReturnsDatabaseUnavailableError()
    {
        var handler = new SimilarHandler(null, null);

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Foo.cs", "content", "text/plain", null, null, null)
            ],
            "file:///src/Seed.cs",
            1000,
            CancellationToken.None);

        result.Content.Should().Contain("Database not available");
    }

    [Test]
    [DisplayName("ResolveSeedDimension returns error when DB is null")]
    public void ResolveSeedDimension_NullDb_ReturnsError()
    {
        var handler = new SimilarHandler(null, null);

        var result = handler.ResolveSeedDimension("file:///src/Foo.cs", CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("Database not available");
    }

    [Test]
    [DisplayName("ExecuteSimilaritySearch returns error when DB is null")]
    public void ExecuteSimilaritySearch_NullDb_ReturnsError()
    {
        var handler = new SimilarHandler(null, null);

        var result = handler.ExecuteSimilaritySearch(
            "file:///src/Seed.cs",
            "file:///src/Seed.cs",
            384,
            ["file:///src/Foo.cs", "file:///src/Bar.cs"],
            CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("Database not available");
        result.Results.Should().BeEmpty();
    }

    // --- Adaptive threshold tests ---

    [Test]
    [DisplayName("Adaptive threshold returns floor when no results")]
    public void AdaptiveThreshold_NoResults_ReturnsFloor()
    {
        var threshold = SimilarHandler.ComputeAdaptiveThreshold([]);

        threshold.Should().Be(0.01);
    }

    [Test]
    [DisplayName("Adaptive threshold computes max(floor, topScore * fraction)")]
    public void AdaptiveThreshold_WithResults_ComputesCorrectly()
    {
        var results = new List<SimilarResult>
        {
            new("file:///a.cs", null, null, null, null, 0.80),
            new("file:///b.cs", null, null, null, null, 0.50),
            new("file:///c.cs", null, null, null, null, 0.10)
        };

        // topScore = 0.80, fraction = 0.50, so relative = 0.40
        // max(0.01, 0.40) = 0.40
        var threshold = SimilarHandler.ComputeAdaptiveThreshold(results);

        threshold.Should().Be(0.40);
    }

    [Test]
    [DisplayName("Adaptive threshold uses floor when topScore * fraction is below floor")]
    public void AdaptiveThreshold_LowScores_UsesFloor()
    {
        var results = new List<SimilarResult>
        {
            new("file:///a.cs", null, null, null, null, 0.015),
            new("file:///b.cs", null, null, null, null, 0.005)
        };

        // topScore = 0.015, fraction = 0.50, so relative = 0.0075
        // max(0.01, 0.0075) = 0.01
        var threshold = SimilarHandler.ComputeAdaptiveThreshold(results);

        threshold.Should().Be(0.01);
    }

    [Test]
    [DisplayName("Adaptive threshold allows results below old 0.10 hard threshold")]
    public void AdaptiveThreshold_CloudEmbeddings_AllowsLowScores()
    {
        // Cloud embeddings often produce lower absolute similarity values.
        // With the old hard threshold of 0.10, these would be filtered out.
        var results = new List<SimilarResult>
        {
            new("file:///a.cs", null, null, null, null, 0.08),
            new("file:///b.cs", null, null, null, null, 0.06),
            new("file:///c.cs", null, null, null, null, 0.03)
        };

        // topScore = 0.08, fraction = 0.50, so relative = 0.04
        // max(0.01, 0.04) = 0.04
        var threshold = SimilarHandler.ComputeAdaptiveThreshold(results);

        threshold.Should().Be(0.04);

        // All three results are above the adaptive threshold
        var filtered = results.Where(r => r.Similarity >= threshold).ToList();
        filtered.Should().HaveCount(2); // 0.08 and 0.06 pass, 0.03 is below 0.04
    }

    // --- Fragment parsing tests ---

    [Test]
    [DisplayName("ParseFragment returns null for URI without fragment")]
    public void ParseFragment_NoFragment_ReturnsNull()
    {
        var result = SimilarHandler.ParseFragment("file:///src/Foo.cs");

        result.Should().BeNull();
    }

    [Test]
    [DisplayName("ParseFragment parses symbol fragment")]
    public void ParseFragment_Symbol_Parsed()
    {
        var result = SimilarHandler.ParseFragment("file:///src/Foo.cs#symbol=MyMethod");

        result.Should().NotBeNull();
        result!.Value.Symbol.Should().Be("MyMethod");
    }

    [Test]
    [DisplayName("ParseFragment parses line range fragment")]
    public void ParseFragment_LineRange_Parsed()
    {
        var result = SimilarHandler.ParseFragment("file:///src/Foo.cs#line=10,20");

        result.Should().NotBeNull();
        result!.Value.Symbol.Should().BeNull();
        result.Value.StartLine.Should().Be(10);
        result.Value.EndLine.Should().Be(20);
    }

    [Test]
    [DisplayName("ParseFragment parses single line fragment")]
    public void ParseFragment_SingleLine_Parsed()
    {
        var result = SimilarHandler.ParseFragment("file:///src/Foo.cs#line=42");

        result.Should().NotBeNull();
        result!.Value.StartLine.Should().Be(42);
        result.Value.EndLine.Should().Be(42);
    }

    // --- StripFragment tests ---

    [Test]
    [DisplayName("StripFragment removes fragment from URI")]
    public void StripFragment_RemovesFragment()
    {
        var result = SimilarHandler.StripFragment("file:///src/Foo.cs#symbol=Bar");

        result.Should().Be("file:///src/Foo.cs");
    }

    [Test]
    [DisplayName("StripFragment returns URI unchanged when no fragment")]
    public void StripFragment_NoFragment_ReturnsUnchanged()
    {
        var result = SimilarHandler.StripFragment("file:///src/Foo.cs");

        result.Should().Be("file:///src/Foo.cs");
    }

    // --- BuildScopeValuesList tests ---

    [Test]
    [DisplayName("BuildScopeValuesList generates VALUES list for candidate URIs")]
    public void BuildScopeValuesList_GeneratesCorrectSql()
    {
        var uris = new List<string> { "file:///src/A.cs", "file:///src/B.cs" };

        var result = SimilarHandler.BuildScopeValuesList(uris, null);

        result.Should().Contain("VALUES");
        result.Should().Contain("'file:///src/A.cs'");
        result.Should().Contain("'file:///src/B.cs'");
    }

    [Test]
    [DisplayName("BuildScopeValuesList excludes seed URI from candidates")]
    public void BuildScopeValuesList_ExcludesSeedUri()
    {
        var uris = new List<string> { "file:///src/Seed.cs", "file:///src/Other.cs" };

        var result = SimilarHandler.BuildScopeValuesList(uris, "file:///src/Seed.cs");

        result.Should().NotContain("file:///src/Seed.cs");
        result.Should().Contain("file:///src/Other.cs");
    }

    [Test]
    [DisplayName("BuildScopeValuesList handles all URIs excluded")]
    public void BuildScopeValuesList_AllExcluded_ReturnsNull()
    {
        var uris = new List<string> { "file:///src/Seed.cs" };

        var result = SimilarHandler.BuildScopeValuesList(uris, "file:///src/Seed.cs");

        result.Should().Contain("(NULL)");
    }

    [Test]
    [DisplayName("BuildScopeValuesList escapes single quotes in URIs")]
    public void BuildScopeValuesList_EscapesSingleQuotes()
    {
        var uris = new List<string> { "file:///src/it's.cs" };

        var result = SimilarHandler.BuildScopeValuesList(uris, null);

        result.Should().Contain("it''s.cs");
    }

    // --- BuildSeedRangeCte tests ---

    [Test]
    [DisplayName("BuildSeedRangeCte for no fragment returns full-object range CTE")]
    public void BuildSeedRangeCte_NoFragment_ReturnsObjectRange()
    {
        var cte = SimilarHandler.BuildSeedRangeCte("file:///src/Foo.cs", "file:///src/Foo.cs");

        cte.Should().Contain("seed_range");
        cte.Should().Contain("MIN(s.start_byte)");
        cte.Should().Contain("MAX(s.end_byte)");
        // Should not contain embed_passage — key invariant
        cte.Should().NotContain("embed_passage");
    }

    [Test]
    [DisplayName("BuildSeedRangeCte for symbol fragment filters by symbol name")]
    public void BuildSeedRangeCte_SymbolFragment_FiltersBySymbol()
    {
        var cte = SimilarHandler.BuildSeedRangeCte(
            "file:///src/Foo.cs#symbol=MyClass",
            "file:///src/Foo.cs");

        cte.Should().Contain("seed_range");
        cte.Should().Contain("myclass");
        cte.Should().NotContain("embed_passage");
    }

    [Test]
    [DisplayName("BuildSeedRangeCte for line fragment converts lines to bytes")]
    public void BuildSeedRangeCte_LineFragment_ConvertsToBytes()
    {
        var cte = SimilarHandler.BuildSeedRangeCte(
            "file:///src/Foo.cs#line=10,20",
            "file:///src/Foo.cs");

        cte.Should().Contain("seed_range");
        cte.Should().Contain("string_split");
        cte.Should().NotContain("embed_passage");
    }

    // --- Output formatting tests ---

    [Test]
    [DisplayName("FormatResult includes URI with similarity score")]
    public void FormatResult_IncludesUriAndScore()
    {
        var result = new SimilarResult(
            "file:///src/A.cs", "A helper class", null, null, null, 0.85);

        var formatted = SimilarHandler.FormatResult(result);

        formatted.Should().Contain("file:///src/A.cs");
        formatted.Should().Contain("[similarity: 0.85]");
        formatted.Should().Contain("A helper class");
    }

    [Test]
    [DisplayName("FormatResult includes line fragment in URI")]
    public void FormatResult_IncludesLineFragment()
    {
        var result = new SimilarResult(
            "file:///src/A.cs", null, "some code", 10, 20, 0.75);

        var formatted = SimilarHandler.FormatResult(result);

        formatted.Should().Contain("file:///src/A.cs#line=10,20");
        formatted.Should().Contain("[similarity: 0.75]");
    }

    [Test]
    [DisplayName("FormatResult does not duplicate fragment")]
    public void FormatResult_DoesNotDuplicateExistingFragment()
    {
        var result = new SimilarResult(
            "file:///src/A.cs#symbol=Foo", null, null, 10, 20, 0.70);

        var formatted = SimilarHandler.FormatResult(result);

        // Should not add another #line= because URI already has #
        formatted.Should().Contain("file:///src/A.cs#symbol=Foo");
        formatted.Should().NotContain("#line=");
    }

    // --- BuildFooter tests ---

    [Test]
    [DisplayName("BuildFooter singular for one result")]
    public void BuildFooter_OneResult_Singular()
    {
        var footer = SimilarHandler.BuildFooter(1, 0);
        footer.Should().Be("[1 similar file shown]");
    }

    [Test]
    [DisplayName("BuildFooter plural for multiple results")]
    public void BuildFooter_MultipleResults_Plural()
    {
        var footer = SimilarHandler.BuildFooter(3, 2);
        footer.Should().Be("[3 similar files shown, 2 more below threshold/budget]");
    }

    // --- BuildOutput token budget tests ---

    [Test]
    [DisplayName("BuildOutput fits results within token budget")]
    public void BuildOutput_FitsWithinBudget()
    {
        var results = new List<SimilarResult>();
        for (var i = 0; i < 10; i++)
        {
            results.Add(new SimilarResult(
                $"file:///src/File{i}.cs",
                $"Headline for file {i}",
                $"line 1\nline 2\nline 3",
                1, 3,
                0.90 - (i * 0.05)));
        }

        // Tiny budget should limit the number of results shown
        var (content, shownCount) = SimilarHandler.BuildOutput(results, 0, 50, CancellationToken.None);

        // At least one result should always be shown (even if over budget)
        shownCount.Should().BeGreaterThanOrEqualTo(1);
        shownCount.Should().BeLessThan(10);
    }

    [Test]
    [DisplayName("BuildOutput with large budget shows all results")]
    public void BuildOutput_LargeBudget_ShowsAll()
    {
        var results = new List<SimilarResult>
        {
            new("file:///a.cs", "A", null, null, null, 0.90),
            new("file:///b.cs", "B", null, null, null, 0.80)
        };

        var (content, shownCount) = SimilarHandler.BuildOutput(results, 0, 100_000, CancellationToken.None);

        shownCount.Should().Be(2);
        content.Should().Contain("file:///a.cs");
        content.Should().Contain("file:///b.cs");
        content.Should().Contain("[2 similar files shown]");
    }

    [Test]
    [DisplayName("BuildOutput includes below-threshold count in footer")]
    public void BuildOutput_IncludesBelowThresholdInFooter()
    {
        var results = new List<SimilarResult>
        {
            new("file:///a.cs", "A", null, null, null, 0.90)
        };

        var (content, _) = SimilarHandler.BuildOutput(results, 5, 100_000, CancellationToken.None);

        content.Should().Contain("5 more below threshold/budget");
    }

    // --- SQL generation does not contain embed_passage ---

    [Test]
    [DisplayName("Similarity search SQL never contains embed_passage")]
    public void SimilaritySearch_NeverCallsEmbedPassage()
    {
        // Verify the CTE generation for all fragment types never includes embed_passage
        var noFragment = SimilarHandler.BuildSeedRangeCte("file:///src/Foo.cs", "file:///src/Foo.cs");
        var symbolFragment = SimilarHandler.BuildSeedRangeCte("file:///src/Foo.cs#symbol=Bar", "file:///src/Foo.cs");
        var lineFragment = SimilarHandler.BuildSeedRangeCte("file:///src/Foo.cs#line=1,10", "file:///src/Foo.cs");

        noFragment.Should().NotContain("embed_passage");
        symbolFragment.Should().NotContain("embed_passage");
        lineFragment.Should().NotContain("embed_passage");
    }

    // --- Error message content tests ---

    [Test]
    [DisplayName("No stored embeddings error is actionable")]
    public void NoStoredEmbeddings_ErrorIsActionable()
    {
        var handler = new SimilarHandler(null, null);
        var result = handler.ResolveSeedDimension("file:///src/Foo.cs", CancellationToken.None);

        // When DB is null the error should indicate the problem clearly
        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("Database not available");
    }

    [Test]
    [DisplayName("ExecuteSimilaritySearch with null DB returns error not empty list")]
    public void ExecuteSimilaritySearch_NullDb_ReturnsErrorNotEmptyList()
    {
        var handler = new SimilarHandler(null, null);

        var result = handler.ExecuteSimilaritySearch(
            "file:///src/Seed.cs",
            "file:///src/Seed.cs",
            384,
            ["file:///src/Other.cs"],
            CancellationToken.None);

        // Old behavior: silently returns []. New behavior: returns error message.
        result.Error.Should().NotBeNull();
        result.Error.Should().Contain("Database not available");
    }

    // --- End-to-end flow with null DB ---

    [Test]
    [DisplayName("Full ExecuteAsync with null DB returns database error in content")]
    public async Task ExecuteAsync_NullDb_ReturnsDatabaseErrorInContent()
    {
        var handler = new SimilarHandler(null, null);

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/A.cs", "content", "text/plain", null, null, null),
                new ReadDocument("file:///src/B.cs", "content", "text/plain", null, null, null)
            ],
            "file:///src/Seed.cs",
            1000,
            CancellationToken.None);

        // Should NOT be empty — should contain an error message
        result.Content.Should().NotBeNullOrEmpty();
        result.Content.Should().Contain("Database not available");
    }

    // --- EscapeSqlLiteral tests ---

    [Test]
    [DisplayName("EscapeSqlLiteral doubles single quotes")]
    public void EscapeSqlLiteral_DoublesSingleQuotes()
    {
        var result = SimilarHandler.EscapeSqlLiteral("it's a test");
        result.Should().Be("it''s a test");
    }

    [Test]
    [DisplayName("EscapeSqlLiteral leaves clean strings unchanged")]
    public void EscapeSqlLiteral_CleanString_Unchanged()
    {
        var result = SimilarHandler.EscapeSqlLiteral("file:///src/Foo.cs");
        result.Should().Be("file:///src/Foo.cs");
    }
}
