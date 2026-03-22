using AwesomeAssertions;
using RepoQL.Contracts;

namespace RepoQL.Tests;

internal class SymbolPatternMatcherTests
{
    // === ParsePattern ===

    [Test]
    public void ParsePattern_NoWildcard_ReturnsNone()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern("MyClass");
        baseSymbol.Should().Be("MyClass");
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.None);
    }

    [Test]
    public void ParsePattern_SingleStar_ReturnsDirectChildren()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern("MyClass.*");
        baseSymbol.Should().Be("MyClass");
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.DirectChildren);
    }

    [Test]
    public void ParsePattern_DoubleStar_ReturnsAllDescendants()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern("MyClass.**");
        baseSymbol.Should().Be("MyClass");
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.AllDescendants);
    }

    [Test]
    public void ParsePattern_NestedWithSingleStar_PreservesPath()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern("Namespace.MyClass.*");
        baseSymbol.Should().Be("Namespace.MyClass");
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.DirectChildren);
    }

    [Test]
    public void ParsePattern_NestedWithDoubleStar_PreservesPath()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern("Namespace.MyClass.**");
        baseSymbol.Should().Be("Namespace.MyClass");
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.AllDescendants);
    }

    [Test]
    public void ParsePattern_EmptyString_ReturnsEmptyNone()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern("");
        baseSymbol.Should().Be(string.Empty);
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.None);
    }

    [Test]
    public void ParsePattern_JustSingleStar_ReturnsEmptyDirectChildren()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern(".*");
        baseSymbol.Should().Be(string.Empty);
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.DirectChildren);
    }

    [Test]
    public void ParsePattern_JustDoubleStar_ReturnsEmptyAllDescendants()
    {
        var (baseSymbol, wildcard) = SymbolPatternMatcher.ParsePattern(".**");
        baseSymbol.Should().Be(string.Empty);
        wildcard.Should().Be(SymbolPatternMatcher.WildcardType.AllDescendants);
    }

    // === Matches - Exact ===

    [Test]
    public void Matches_ExactMatch_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("MyClass", "MyClass").Should().BeTrue();
    }

    [Test]
    public void Matches_ExactMatch_CaseInsensitive()
    {
        SymbolPatternMatcher.Matches("MyClass", "myclass").Should().BeTrue();
        SymbolPatternMatcher.Matches("myclass", "MyClass").Should().BeTrue();
    }

    [Test]
    public void Matches_ExactMatch_CaseSensitive_WhenIgnoreCaseFalse()
    {
        SymbolPatternMatcher.Matches("MyClass", "myclass", ignoreCase: false).Should().BeFalse();
        SymbolPatternMatcher.Matches("MyClass", "MyClass", ignoreCase: false).Should().BeTrue();
    }

    [Test]
    public void Matches_ExactNoMatch_ReturnsFalse()
    {
        SymbolPatternMatcher.Matches("MyClass", "OtherClass").Should().BeFalse();
    }

    [Test]
    public void Matches_ExactNestedMatch_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("Namespace.MyClass.Method", "Namespace.MyClass.Method").Should().BeTrue();
    }

    // === Matches - Direct Children (.*) ===

    [Test]
    public void Matches_DirectChild_WithStar_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("MyClass.Method", "MyClass.*").Should().BeTrue();
    }

    [Test]
    public void Matches_DirectChild_Field_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("MyClass.Field", "MyClass.*").Should().BeTrue();
    }

    [Test]
    public void Matches_DirectChild_Property_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("MyClass.Property", "MyClass.*").Should().BeTrue();
    }

    [Test]
    public void Matches_NestedChild_WithStar_ReturnsFalse()
    {
        // MyClass.Inner.Method is NOT a direct child of MyClass
        SymbolPatternMatcher.Matches("MyClass.Inner.Method", "MyClass.*").Should().BeFalse();
    }

    [Test]
    public void Matches_Parent_WithStar_ReturnsFalse()
    {
        // MyClass itself is not a child of MyClass
        SymbolPatternMatcher.Matches("MyClass", "MyClass.*").Should().BeFalse();
    }

    [Test]
    public void Matches_DirectChild_CaseInsensitive()
    {
        SymbolPatternMatcher.Matches("MYCLASS.METHOD", "myclass.*").Should().BeTrue();
    }

    [Test]
    public void Matches_DirectChild_CaseSensitive_WhenIgnoreCaseFalse()
    {
        SymbolPatternMatcher.Matches("MYCLASS.METHOD", "myclass.*", ignoreCase: false).Should().BeFalse();
        SymbolPatternMatcher.Matches("MyClass.Method", "MyClass.*", ignoreCase: false).Should().BeTrue();
    }

    [Test]
    public void Matches_DirectChild_NestedParent_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("Namespace.MyClass.Method", "Namespace.MyClass.*").Should().BeTrue();
    }

    [Test]
    public void Matches_DirectChild_FindsParentAtSegmentBoundary()
    {
        SymbolPatternMatcher.Matches("Namespace.MyClass.Method", "MyClass.*").Should().BeTrue();
    }

    [Test]
    public void Matches_DirectChild_WrongParent_ReturnsFalse()
    {
        SymbolPatternMatcher.Matches("OtherClass.Method", "MyClass.*").Should().BeFalse();
    }

    // === Matches - All Descendants (.**) ===

    [Test]
    public void Matches_Descendant_DirectChild_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("MyClass.Method", "MyClass.**").Should().BeTrue();
    }

    [Test]
    public void Matches_Descendant_NestedChild_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("MyClass.Inner.Method", "MyClass.**").Should().BeTrue();
    }

    [Test]
    public void Matches_Descendant_DeeplyNested_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("MyClass.Inner.Deep.VeryDeep.Method", "MyClass.**").Should().BeTrue();
    }

    [Test]
    public void Matches_Descendant_Parent_ReturnsFalse()
    {
        // MyClass itself is not a descendant of MyClass
        SymbolPatternMatcher.Matches("MyClass", "MyClass.**").Should().BeFalse();
    }

    [Test]
    public void Matches_Descendant_CaseInsensitive()
    {
        SymbolPatternMatcher.Matches("MYCLASS.INNER.METHOD", "myclass.**").Should().BeTrue();
    }

    [Test]
    public void Matches_Descendant_NestedParent_ReturnsTrue()
    {
        SymbolPatternMatcher.Matches("Namespace.MyClass.Inner.Method", "Namespace.MyClass.**").Should().BeTrue();
    }

    [Test]
    public void Matches_Descendant_FindsAncestorAtSegmentBoundary()
    {
        SymbolPatternMatcher.Matches("Company.Product.MyClass.Inner.Method", "MyClass.**").Should().BeTrue();
    }

    [Test]
    public void Matches_Descendant_WrongParent_ReturnsFalse()
    {
        SymbolPatternMatcher.Matches("OtherClass.Method", "MyClass.**").Should().BeFalse();
    }

    // === Edge Cases ===

    [Test]
    public void Matches_NullQualifiedName_ReturnsFalse()
    {
        SymbolPatternMatcher.Matches(null!, "MyClass.*").Should().BeFalse();
    }

    [Test]
    public void Matches_EmptyQualifiedName_ReturnsFalse()
    {
        SymbolPatternMatcher.Matches("", "MyClass.*").Should().BeFalse();
    }

    [Test]
    public void Matches_NullPattern_ReturnsFalse()
    {
        SymbolPatternMatcher.Matches("MyClass.Method", null!).Should().BeFalse();
    }

    [Test]
    public void Matches_EmptyPattern_ReturnsFalse()
    {
        SymbolPatternMatcher.Matches("MyClass.Method", "").Should().BeFalse();
    }

    [Test]
    public void Matches_EmptyBaseSymbol_DirectChildren_MatchesSingleLevel()
    {
        // ".*" with empty base matches any single-level name
        SymbolPatternMatcher.Matches("TopLevel", ".*").Should().BeTrue();
        SymbolPatternMatcher.Matches("Has.Dots", ".*").Should().BeFalse();
    }

    [Test]
    public void Matches_EmptyBaseSymbol_AllDescendants_MatchesNested()
    {
        // ".**" with empty base matches any nested name
        SymbolPatternMatcher.Matches("Has.Dots", ".**").Should().BeTrue();
        SymbolPatternMatcher.Matches("TopLevel", ".**").Should().BeFalse();
    }

    [Test]
    public void Matches_PartialParentMatch_ReturnsFalse()
    {
        // "My" is not the same as "MyClass"
        SymbolPatternMatcher.Matches("MyClass.Method", "My.*").Should().BeFalse();
    }

    [Test]
    public void Matches_ParentPrefixMatch_ReturnsFalse()
    {
        // "MyClassExtra.Method" should not match "MyClass.*"
        SymbolPatternMatcher.Matches("MyClassExtra.Method", "MyClass.*").Should().BeFalse();
    }

    // === Real-World Scenarios ===

    [Test]
    public void Matches_CSharpClass_AllMembers()
    {
        var pattern = "RepoQL.Contracts.RepoUri.**";

        // Direct members
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.TryParse", pattern).Should().BeTrue();
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.Fragment", pattern).Should().BeTrue();

        // Nested type members
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.Location.Symbol", pattern).Should().BeTrue();
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.Location.Line", pattern).Should().BeTrue();

        // The class itself
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri", pattern).Should().BeFalse();

        // Different class
        SymbolPatternMatcher.Matches("RepoQL.Contracts.OtherClass.Method", pattern).Should().BeFalse();
    }

    [Test]
    public void Matches_CSharpClass_DirectMembersOnly()
    {
        var pattern = "RepoQL.Contracts.RepoUri.*";

        // Direct members
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.TryParse", pattern).Should().BeTrue();
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.Fragment", pattern).Should().BeTrue();
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.Location", pattern).Should().BeTrue();

        // Nested type members - NOT direct children
        SymbolPatternMatcher.Matches("RepoQL.Contracts.RepoUri.Location.Symbol", pattern).Should().BeFalse();
    }

    [Test]
    public void Matches_Issue68_ShortClassName_DirectChildren()
    {
        var pattern = "DuckDbDataStore.*";
        SymbolPatternMatcher.Matches("RepoQL.Data.DuckDB.DuckDbDataStore.Query", pattern).Should().BeTrue();
        SymbolPatternMatcher.Matches("RepoQL.Data.DuckDB.DuckDbDataStore.GetConnection", pattern).Should().BeTrue();
        SymbolPatternMatcher.Matches("RepoQL.Data.DuckDB.DuckDbDataStore", pattern).Should().BeFalse();
        SymbolPatternMatcher.Matches("RepoQL.Data.DuckDB.OtherClass.Query", pattern).Should().BeFalse();
    }
}
