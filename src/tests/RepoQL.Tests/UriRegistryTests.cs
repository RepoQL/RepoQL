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

    [Test]
    public void MatchPattern_AnchorFragment_MatchesHeadingUri()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///docs/north-star/formats.md");
        var headingUri = RepoUri.Parse("file:///docs/north-star/formats.md#legibility");

        registry.SetIndexed(fileUri, lineCount: 120, new Dictionary<RepoUri, SymbolEntry>
        {
            { headingUri, new SymbolEntry("md_heading", 42, 68) }
        }.AsReadOnly());

        var matches = registry.MatchPattern("file:///docs/north-star/formats.md#legibility").ToList();

        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Be("file:///docs/north-star/formats.md#legibility");
    }

    [Test]
    public void MatchPattern_AnchorFragment_WildcardMatchesHeadingUri()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///docs/north-star/formats.md");
        var headingUri = RepoUri.Parse("file:///docs/north-star/formats.md#legibility");

        registry.SetIndexed(fileUri, lineCount: 120, new Dictionary<RepoUri, SymbolEntry>
        {
            { headingUri, new SymbolEntry("md_heading", 42, 68) }
        }.AsReadOnly());

        var matches = registry.MatchPattern("file:///docs/north-star/formats.md#legi*").ToList();

        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Be("file:///docs/north-star/formats.md#legibility");
    }

    // === Line Range Globbing Tests ===

    [Test]
    public void MatchPattern_LineRangeExclusion_SubtractsFromSymbol()
    {
        var registry = CreateTestRegistry();

        // MyClass is lines 10-50. Exclude lines 10-15, should return partial range.
        var matches = registry.MatchPattern("file:///src/App.cs#symbol=MyClass;!#line=10,15").ToList();

        matches.Should().HaveCount(1);
        // Remaining should be lines 16-50
        matches.Single().AbsoluteUri.Should().Contain("#line=16,50");
    }

    [Test]
    public void MatchPattern_LineRangeExclusion_SplitsSymbol()
    {
        var registry = CreateTestRegistry();

        // MyClass is lines 10-50. Exclude lines 25-30, should split into two ranges.
        var matches = registry.MatchPattern("file:///src/App.cs#symbol=MyClass;!#line=25,30").ToList();

        matches.Should().HaveCount(2);
        // Should have lines 10-24 and 31-50
        matches.Should().Contain(m => m.AbsoluteUri.Contains("#line=10,24"));
        matches.Should().Contain(m => m.AbsoluteUri.Contains("#line=31,50"));
    }

    [Test]
    public void MatchPattern_LineRangeExclusion_NoOverlap_ReturnsUnchanged()
    {
        var registry = CreateTestRegistry();

        // MyClass is lines 10-50. Exclude lines 60-70, no overlap.
        var matches = registry.MatchPattern("file:///src/App.cs#symbol=MyClass;!#line=60,70").ToList();

        matches.Should().HaveCount(1);
        // Should return the symbol unchanged since exclusion doesn't overlap
        matches.Single().AbsoluteUri.Should().Contain("#symbol=MyClass");
    }

    [Test]
    public void MatchPattern_LineRangeExclusion_FullCover_ReturnsNothing()
    {
        var registry = CreateTestRegistry();

        // MyClass is lines 10-50. Exclude lines 1-100, fully covers.
        var matches = registry.MatchPattern("file:///src/App.cs#symbol=MyClass;!#line=1,100").ToList();

        matches.Should().BeEmpty();
    }

    [Test]
    public void MatchPattern_AllSymbolsMinusHeader_ReturnsPartialRanges()
    {
        var registry = CreateTestRegistry();

        // Get all symbols, exclude first 12 lines (header region)
        // MyClass (10-50) should become partial, MyClassHelper (55-90) unchanged
        var matches = registry.MatchPattern("file:///src/App.cs#symbol=*;!#line=1,12").ToList();

        matches.Should().HaveCount(2);
        // MyClass should be trimmed to 13-50
        matches.Should().Contain(m => m.AbsoluteUri.Contains("#line=13,50"));
        // MyClassHelper should be unchanged (55-90 - exact match returns symbol)
        matches.Should().Contain(m => m.AbsoluteUri.Contains("#symbol=MyClassHelper"));
    }

    [Test]
    public void MatchPattern_WholeFile_ReturnsFileUri()
    {
        var registry = CreateTestRegistry();

        // README.md is 20 lines, no symbols. Matching it should return file URI.
        var matches = registry.MatchPattern("file:///README.md").ToList();

        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Be("file:///README.md");
    }

    [Test]
    public void MatchPattern_ExplicitLineRange_ReturnsLineRangeUri()
    {
        var registry = CreateTestRegistry();

        // Request specific line range
        var matches = registry.MatchPattern("file:///src/App.cs#line=5,15").ToList();

        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Contain("#line=5,15");
    }

    // === UriSimplifier Tests ===

    [Test]
    public void UriSimplifier_WholeFile_ReturnsFileUri()
    {
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var entry = new FileEntry(
            Status: UriStatus.Indexed,
            IndexedAt: DateTime.UtcNow,
            Error: null,
            EmbeddingStatus: EmbeddingStatus.Pending,
            EmbeddedChunkCount: 0,
            EmbeddedAt: null,
            LineCount: 100,
            Symbols: new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        var result = UriSimplifier.Simplify(fileUri, new LineRange(1, 100), entry);

        result.AbsoluteUri.Should().Be("file:///src/App.cs");
    }

    [Test]
    public void UriSimplifier_ExactSymbolMatch_ReturnsSymbolUri()
    {
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var symbolUri = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");
        var entry = new FileEntry(
            Status: UriStatus.Indexed,
            IndexedAt: DateTime.UtcNow,
            Error: null,
            EmbeddingStatus: EmbeddingStatus.Pending,
            EmbeddedChunkCount: 0,
            EmbeddedAt: null,
            LineCount: 100,
            Symbols: new Dictionary<RepoUri, SymbolEntry>
            {
                { symbolUri, new SymbolEntry("class", 10, 50) }
            }.AsReadOnly());

        var result = UriSimplifier.Simplify(fileUri, new LineRange(10, 50), entry);

        result.AbsoluteUri.Should().Be(symbolUri.AbsoluteUri);
    }

    [Test]
    public void UriSimplifier_PartialRange_ReturnsLineRangeUri()
    {
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var entry = new FileEntry(
            Status: UriStatus.Indexed,
            IndexedAt: DateTime.UtcNow,
            Error: null,
            EmbeddingStatus: EmbeddingStatus.Pending,
            EmbeddedChunkCount: 0,
            EmbeddedAt: null,
            LineCount: 100,
            Symbols: new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        var result = UriSimplifier.Simplify(fileUri, new LineRange(25, 75), entry);

        result.AbsoluteUri.Should().Be("file:///src/App.cs#line=25,75");
    }

    [Test]
    public void UriSimplifier_InvalidRange_ReturnsFileUri()
    {
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var entry = new FileEntry(
            Status: UriStatus.Indexed,
            IndexedAt: DateTime.UtcNow,
            Error: null,
            EmbeddingStatus: EmbeddingStatus.Pending,
            EmbeddedChunkCount: 0,
            EmbeddedAt: null,
            LineCount: 100,
            Symbols: new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        var result = UriSimplifier.Simplify(fileUri, LineRange.Empty, entry);

        result.AbsoluteUri.Should().Be("file:///src/App.cs");
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

        // File 1: App.cs with 2 symbols (100 lines)
        var appUri = RepoUri.Parse("file:///src/App.cs");
        var symbol1 = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");
        var symbol2 = RepoUri.Parse("file:///src/App.cs#symbol=MyClassHelper");
        registry.SetIndexed(appUri, lineCount: 100, new Dictionary<RepoUri, SymbolEntry>
        {
            { symbol1, new SymbolEntry("type", 10, 50) },
            { symbol2, new SymbolEntry("type", 55, 90) }
        }.AsReadOnly());

        // File 2: Utils.cs with 1 symbol (80 lines)
        var utilsUri = RepoUri.Parse("file:///src/Utils.cs");
        var symbol3 = RepoUri.Parse("file:///src/Utils.cs#symbol=StringUtils");
        registry.SetIndexed(utilsUri, lineCount: 80, new Dictionary<RepoUri, SymbolEntry>
        {
            { symbol3, new SymbolEntry("type", 5, 75) }
        }.AsReadOnly());

        // File 3: README.md with no symbols (20 lines)
        var readmeUri = RepoUri.Parse("file:///README.md");
        registry.SetIndexed(readmeUri, lineCount: 20, new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        return registry;
    }

    // === SymbolEntry Tests ===

    [Test]
    public void SymbolEntry_WithSpan_HasSpanIsTrue()
    {
        var entry = new SymbolEntry("class", 10, 50);

        entry.HasSpan.Should().BeTrue();
        entry.Kind.Should().Be("class");
        entry.StartLine.Should().Be(10);
        entry.EndLine.Should().Be(50);
    }

    [Test]
    public void SymbolEntry_WithKindOnly_HasSpanIsFalse()
    {
        var entry = SymbolEntry.WithKindOnly("method");

        entry.HasSpan.Should().BeFalse();
        entry.Kind.Should().Be("method");
        entry.StartLine.Should().Be(0);
        entry.EndLine.Should().Be(0);
    }

    [Test]
    public void SetIndexed_WithSymbolEntries_StoresSpans()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var symbolUri = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");

        var symbols = new Dictionary<RepoUri, SymbolEntry>
        {
            { symbolUri, new SymbolEntry("class", 10, 80) }
        }.AsReadOnly();

        registry.SetIndexed(fileUri, lineCount: 100, symbols);

        registry[fileUri].LineCount.Should().Be(100);
        registry[fileUri].Symbols[symbolUri].StartLine.Should().Be(10);
        registry[fileUri].Symbols[symbolUri].EndLine.Should().Be(80);
        registry[fileUri].Symbols[symbolUri].HasSpan.Should().BeTrue();
    }

    [Test]
    public void SetIndexed_BackwardCompatible_ConvertsToSymbolEntry()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var symbolUri = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");

        // Use old signature with string dictionary
        var symbols = new Dictionary<RepoUri, string> { { symbolUri, "type" } };
        registry.SetIndexed(fileUri, symbols.AsReadOnly());

        // Should have converted to SymbolEntry with no span
        registry[fileUri].LineCount.Should().Be(0);
        registry[fileUri].Symbols[symbolUri].Kind.Should().Be("type");
        registry[fileUri].Symbols[symbolUri].HasSpan.Should().BeFalse();
    }

    // === LineRange Tests ===

    [Test]
    public void LineRange_ValidRange_PropertiesCorrect()
    {
        var range = new LineRange(10, 20);

        range.Start.Should().Be(10);
        range.End.Should().Be(20);
        range.Length.Should().Be(11);
        range.IsEmpty.Should().BeFalse();
        range.IsValid.Should().BeTrue();
    }

    [Test]
    public void LineRange_Empty_IsEmptyTrue()
    {
        var range = LineRange.Empty;

        range.IsEmpty.Should().BeTrue();
        range.IsValid.Should().BeFalse();
        range.Length.Should().Be(0);
    }

    [Test]
    public void LineRange_InvalidStartGreaterThanEnd_IsEmptyTrue()
    {
        var range = new LineRange(20, 10);

        range.IsEmpty.Should().BeTrue();
        range.IsValid.Should().BeFalse();
    }

    [Test]
    public void LineRange_Overlaps_OverlappingRanges_ReturnsTrue()
    {
        var range1 = new LineRange(10, 30);
        var range2 = new LineRange(25, 45);

        range1.Overlaps(range2).Should().BeTrue();
        range2.Overlaps(range1).Should().BeTrue();
    }

    [Test]
    public void LineRange_Overlaps_NonOverlapping_ReturnsFalse()
    {
        var range1 = new LineRange(10, 20);
        var range2 = new LineRange(30, 40);

        range1.Overlaps(range2).Should().BeFalse();
        range2.Overlaps(range1).Should().BeFalse();
    }

    [Test]
    public void LineRange_Overlaps_Adjacent_ReturnsFalse()
    {
        var range1 = new LineRange(10, 20);
        var range2 = new LineRange(21, 30);

        range1.Overlaps(range2).Should().BeFalse();
    }

    [Test]
    public void LineRange_Contains_FullyContained_ReturnsTrue()
    {
        var outer = new LineRange(10, 50);
        var inner = new LineRange(20, 40);

        outer.Contains(inner).Should().BeTrue();
        inner.Contains(outer).Should().BeFalse();
    }

    [Test]
    public void LineRange_IsAdjacentTo_AdjacentRanges_ReturnsTrue()
    {
        var range1 = new LineRange(10, 20);
        var range2 = new LineRange(21, 30);

        range1.IsAdjacentTo(range2).Should().BeTrue();
        range2.IsAdjacentTo(range1).Should().BeTrue();
    }

    [Test]
    public void LineRange_WholeFile_CreatesCorrectRange()
    {
        var range = LineRange.WholeFile(200);

        range.Start.Should().Be(1);
        range.End.Should().Be(200);
        range.Length.Should().Be(200);
    }

    [Test]
    public void LineRange_SingleLine_CreatesCorrectRange()
    {
        var range = LineRange.SingleLine(42);

        range.Start.Should().Be(42);
        range.End.Should().Be(42);
        range.Length.Should().Be(1);
    }

    // === LineRangeCalculator.Union Tests ===

    [Test]
    public void Union_EmptyInput_ReturnsEmpty()
    {
        var ranges = Array.Empty<LineRange>();

        var result = ranges.Union();

        result.Should().BeEmpty();
    }

    [Test]
    public void Union_SingleRange_ReturnsSameRange()
    {
        var ranges = new[] { new LineRange(10, 20) };

        var result = ranges.Union();

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(10, 20));
    }

    [Test]
    public void Union_OverlappingRanges_MergesIntoOne()
    {
        var ranges = new[] { new LineRange(10, 30), new LineRange(25, 45) };

        var result = ranges.Union();

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(10, 45));
    }

    [Test]
    public void Union_AdjacentRanges_MergesIntoOne()
    {
        var ranges = new[] { new LineRange(10, 20), new LineRange(21, 30) };

        var result = ranges.Union();

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(10, 30));
    }

    [Test]
    public void Union_NonOverlappingRanges_PreservesBoth()
    {
        var ranges = new[] { new LineRange(10, 20), new LineRange(30, 40) };

        var result = ranges.Union();

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineRange(10, 20));
        result[1].Should().Be(new LineRange(30, 40));
    }

    [Test]
    public void Union_UnsortedRanges_ReturnsSorted()
    {
        var ranges = new[] { new LineRange(50, 60), new LineRange(10, 20), new LineRange(30, 40) };

        var result = ranges.Union();

        result.Should().HaveCount(3);
        result[0].Should().Be(new LineRange(10, 20));
        result[1].Should().Be(new LineRange(30, 40));
        result[2].Should().Be(new LineRange(50, 60));
    }

    [Test]
    public void Union_MultipleOverlapping_MergesAll()
    {
        var ranges = new[]
        {
            new LineRange(10, 30),
            new LineRange(25, 45),
            new LineRange(40, 60),
            new LineRange(55, 70)
        };

        var result = ranges.Union();

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(10, 70));
    }

    [Test]
    public void Union_InvalidRanges_AreFiltered()
    {
        var ranges = new[]
        {
            new LineRange(10, 20),
            new LineRange(30, 10), // Invalid: start > end
            LineRange.Empty,
            new LineRange(40, 50)
        };

        var result = ranges.Union();

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineRange(10, 20));
        result[1].Should().Be(new LineRange(40, 50));
    }

    // === LineRangeCalculator.Subtract Tests ===

    [Test]
    public void Subtract_EmptyIncluded_ReturnsEmpty()
    {
        var included = Array.Empty<LineRange>().ToList();
        var excluded = new List<LineRange> { new(30, 40) };

        var result = included.Subtract(excluded);

        result.Should().BeEmpty();
    }

    [Test]
    public void Subtract_EmptyExcluded_ReturnsIncluded()
    {
        var included = new List<LineRange> { new(10, 80) };
        var excluded = Array.Empty<LineRange>().ToList();

        var result = included.Subtract(excluded);

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(10, 80));
    }

    [Test]
    public void Subtract_MiddleExclusion_SplitsRange()
    {
        var included = new List<LineRange> { new(10, 80) };
        var excluded = new List<LineRange> { new(30, 40) };

        var result = included.Subtract(excluded);

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineRange(10, 29));
        result[1].Should().Be(new LineRange(41, 80));
    }

    [Test]
    public void Subtract_StartExclusion_TrimsStart()
    {
        var included = new List<LineRange> { new(10, 80) };
        var excluded = new List<LineRange> { new(10, 30) };

        var result = included.Subtract(excluded);

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(31, 80));
    }

    [Test]
    public void Subtract_EndExclusion_TrimsEnd()
    {
        var included = new List<LineRange> { new(10, 80) };
        var excluded = new List<LineRange> { new(60, 80) };

        var result = included.Subtract(excluded);

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(10, 59));
    }

    [Test]
    public void Subtract_FullExclusion_ReturnsEmpty()
    {
        var included = new List<LineRange> { new(10, 80) };
        var excluded = new List<LineRange> { new(10, 80) };

        var result = included.Subtract(excluded);

        result.Should().BeEmpty();
    }

    [Test]
    public void Subtract_LargerExclusion_ReturnsEmpty()
    {
        var included = new List<LineRange> { new(20, 40) };
        var excluded = new List<LineRange> { new(10, 80) };

        var result = included.Subtract(excluded);

        result.Should().BeEmpty();
    }

    [Test]
    public void Subtract_MultipleExclusions_CarvesBoth()
    {
        var included = new List<LineRange> { new(10, 80) };
        var excluded = new List<LineRange> { new(20, 30), new(50, 60) };

        var result = included.Subtract(excluded);

        result.Should().HaveCount(3);
        result[0].Should().Be(new LineRange(10, 19));
        result[1].Should().Be(new LineRange(31, 49));
        result[2].Should().Be(new LineRange(61, 80));
    }

    [Test]
    public void Subtract_NonOverlappingExclusion_NoEffect()
    {
        var included = new List<LineRange> { new(10, 20) };
        var excluded = new List<LineRange> { new(30, 40) };

        var result = included.Subtract(excluded);

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(10, 20));
    }

    [Test]
    public void Subtract_SingleLineRange_Works()
    {
        var included = new List<LineRange> { new(5, 5) };
        var excluded = new List<LineRange> { new(5, 5) };

        var result = included.Subtract(excluded);

        result.Should().BeEmpty();
    }

    [Test]
    public void Subtract_ExclusionAtLine1_Works()
    {
        var included = new List<LineRange> { new(1, 100) };
        var excluded = new List<LineRange> { new(1, 30) };

        var result = included.Subtract(excluded);

        result.Should().HaveCount(1);
        result[0].Should().Be(new LineRange(31, 100));
    }

    [Test]
    public void Subtract_MultipleIncludedRanges_ProcessesAll()
    {
        var included = new List<LineRange> { new(10, 30), new(50, 70) };
        var excluded = new List<LineRange> { new(20, 60) };

        var result = included.Subtract(excluded);

        result.Should().HaveCount(2);
        result[0].Should().Be(new LineRange(10, 19));
        result[1].Should().Be(new LineRange(61, 70));
    }

    // === Spanless Symbol Tests ===

    [Test]
    public void MatchPattern_SpanlessSymbol_ReturnsSymbolUri()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var symbolUri = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");

        // Register file with spanless symbol (HasSpan = false)
        registry.SetIndexed(fileUri, lineCount: 100, new Dictionary<RepoUri, SymbolEntry>
        {
            { symbolUri, SymbolEntry.WithKindOnly("class") }
        }.AsReadOnly());

        var matches = registry.MatchPattern("src/**/*.cs#symbol=MyClass").ToList();

        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Be("file:///src/App.cs#symbol=MyClass");
    }

    [Test]
    public void MatchPattern_SpanlessSymbol_WithNegativePattern_IsExcluded()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var symbol1 = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");
        var symbol2 = RepoUri.Parse("file:///src/App.cs#symbol=Helper");

        // Register file with two spanless symbols
        registry.SetIndexed(fileUri, lineCount: 100, new Dictionary<RepoUri, SymbolEntry>
        {
            { symbol1, SymbolEntry.WithKindOnly("class") },
            { symbol2, SymbolEntry.WithKindOnly("class") }
        }.AsReadOnly());

        // Match all symbols except Helper
        var matches = registry.MatchPattern("src/**/*.cs#symbol=*;!#symbol=Helper").ToList();

        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Be("file:///src/App.cs#symbol=MyClass");
    }

    [Test]
    public void MatchPattern_MixedSpanAndSpanlessSymbols_ReturnsBoth()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var symbolWithSpan = RepoUri.Parse("file:///src/App.cs#symbol=MyClass");
        var symbolWithoutSpan = RepoUri.Parse("file:///src/App.cs#symbol=Helper");

        // One symbol has span, one doesn't
        registry.SetIndexed(fileUri, lineCount: 100, new Dictionary<RepoUri, SymbolEntry>
        {
            { symbolWithSpan, new SymbolEntry("class", 10, 50) },
            { symbolWithoutSpan, SymbolEntry.WithKindOnly("function") }
        }.AsReadOnly());

        var matches = registry.MatchPattern("src/**/*.cs#symbol=*").ToList();

        matches.Should().HaveCount(2);
        matches.Should().Contain(m => m.AbsoluteUri.Contains("#symbol=MyClass"));
        matches.Should().Contain(m => m.AbsoluteUri.Contains("#symbol=Helper"));
    }

    // === Files with LineCount == 0 Tests ===

    [Test]
    public void MatchPattern_FileWithZeroLineCount_ReturnsFileUri()
    {
        var registry = new UriRegistry();
        var fileUri = RepoUri.Parse("file:///src/binary.bin");

        // File with LineCount == 0 (e.g., binary file or unknown size)
        registry.SetIndexed(fileUri, lineCount: 0, new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        var matches = registry.MatchPattern("src/**").ToList();

        matches.Should().HaveCount(1);
        // Should return file URI, not #line=1,2147483647
        matches.Single().AbsoluteUri.Should().Be("file:///src/binary.bin");
    }

    [Test]
    public void MatchPattern_NegativeOnly_FileWithZeroLineCount_NotDropped()
    {
        var registry = new UriRegistry();
        var file1 = RepoUri.Parse("file:///src/keep.bin");
        var file2 = RepoUri.Parse("file:///src/drop.txt");

        // Both files have LineCount == 0
        registry.SetIndexed(file1, lineCount: 0, new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());
        registry.SetIndexed(file2, lineCount: 0, new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        // Negative-only pattern: exclude .txt files
        var matches = registry.MatchPattern("!**/*.txt").ToList();

        // Should include keep.bin, not drop.txt
        matches.Should().HaveCount(1);
        matches.Single().AbsoluteUri.Should().Be("file:///src/keep.bin");
    }

    [Test]
    public void LineRange_WholeFileUnknown_Properties()
    {
        var range = LineRange.WholeFileUnknown;

        range.Start.Should().Be(1);
        range.End.Should().Be(int.MaxValue);
        range.IsWholeFileUnknown.Should().BeTrue();
        range.IsValid.Should().BeTrue();
    }

    [Test]
    public void LineRange_WholeFile_ZeroLineCount_ReturnsWholeFileUnknown()
    {
        var range = LineRange.WholeFile(0);

        range.IsWholeFileUnknown.Should().BeTrue();
        range.IsValid.Should().BeTrue();
    }

    [Test]
    public void UriSimplifier_WholeFileUnknown_ReturnsFileUri()
    {
        var fileUri = RepoUri.Parse("file:///src/App.cs");
        var entry = new FileEntry(
            Status: UriStatus.Indexed,
            IndexedAt: DateTime.UtcNow,
            Error: null,
            EmbeddingStatus: EmbeddingStatus.Pending,
            EmbeddedChunkCount: 0,
            EmbeddedAt: null,
            LineCount: 0, // Unknown line count
            Symbols: new Dictionary<RepoUri, SymbolEntry>().AsReadOnly());

        var result = UriSimplifier.Simplify(fileUri, LineRange.WholeFileUnknown, entry);

        // Should return file URI, not a line range
        result.AbsoluteUri.Should().Be("file:///src/App.cs");
    }
}
