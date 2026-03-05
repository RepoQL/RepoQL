using AwesomeAssertions;
using RepoQL.Formats.DotNet.TreeSitter;

namespace RepoQL.Formats.DotNet.Tests;

public sealed class CSharpTreeSitterQueryTests
{
    [Test]
    public void CombinedQuery_CompilesSuccessfully()
    {
        // Validates that all 16 patterns in the combined query form a valid tree-sitter query.
        using var language = new global::TreeSitter.Language("tree-sitter-c-sharp", "tree_sitter_c_sharp");
        using var query = language.CreateQuery(CSharpQueries.CombinedQuery);

        // If we get here without exception, the combined query compiles.
        // Verify it can execute against trivial input.
        using var parser = new global::TreeSitter.Parser(language);
        using var tree = parser.Parse("using System;");
        using var cursor = query.Execute(tree.RootNode);
        cursor.Matches.Should().NotBeEmpty();
    }

    [Test]
    public void CombinedQuery_MatchesAllPatternGroups()
    {
        // A comprehensive C# file that exercises every pattern group.
        const string source = """
            using System;
            using static System.Math;

            namespace Example.Test
            {
                public class Foo : IBar
                {
                    private readonly string _name;

                    public Foo(string name) { _name = name; }

                    public string Name { get; set; }

                    public int Calculate(int x) => x * 2;

                    public event EventHandler Changed;

                    public string this[int index] => _name;
                }

                public struct Point { public int X; public int Y; }

                public record Person(string Name, int Age);

                public interface IBar { void DoSomething(); }

                public enum Color { Red, Green, Blue }
            }
            """;

        using var language = new global::TreeSitter.Language("tree-sitter-c-sharp", "tree_sitter_c_sharp");
        using var query = language.CreateQuery(CSharpQueries.CombinedQuery);
        using var parser = new global::TreeSitter.Parser(language);
        using var tree = parser.Parse(source);
        using var cursor = query.Execute(tree.RootNode);

        var matchedGroups = cursor.Matches
            .Select(m => CSharpQueries.ClassifyPattern(m.PatternIndex))
            .Distinct()
            .ToHashSet();

        // Every pattern group except Comments should match (no comments in sample).
        matchedGroups.Should().Contain(CSharpPatternGroup.UsingDirectives);
        matchedGroups.Should().Contain(CSharpPatternGroup.NamespaceDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.ClassDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.StructDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.RecordDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.InterfaceDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.EnumDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.MethodDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.ConstructorDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.PropertyDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.FieldDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.EventDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.IndexerDeclarations);
    }

    [Test]
    public void CombinedQuery_MatchesComments()
    {
        const string source = """
            // A single-line comment
            /// <summary>A doc comment</summary>
            /* A block comment */
            public class Foo { }
            """;

        using var language = new global::TreeSitter.Language("tree-sitter-c-sharp", "tree_sitter_c_sharp");
        using var query = language.CreateQuery(CSharpQueries.CombinedQuery);
        using var parser = new global::TreeSitter.Parser(language);
        using var tree = parser.Parse(source);
        using var cursor = query.Execute(tree.RootNode);

        var matchedGroups = cursor.Matches
            .Select(m => CSharpQueries.ClassifyPattern(m.PatternIndex))
            .Distinct()
            .ToHashSet();

        matchedGroups.Should().Contain(CSharpPatternGroup.Comments);
    }

    [Test]
    public void CombinedQuery_FileScopedNamespace()
    {
        const string source = """
            namespace Example.Test;

            public class Foo { }
            """;

        using var language = new global::TreeSitter.Language("tree-sitter-c-sharp", "tree_sitter_c_sharp");
        using var query = language.CreateQuery(CSharpQueries.CombinedQuery);
        using var parser = new global::TreeSitter.Parser(language);
        using var tree = parser.Parse(source);
        using var cursor = query.Execute(tree.RootNode);

        var matchedGroups = cursor.Matches
            .Select(m => CSharpQueries.ClassifyPattern(m.PatternIndex))
            .Distinct()
            .ToHashSet();

        matchedGroups.Should().Contain(CSharpPatternGroup.NamespaceDeclarations);
        matchedGroups.Should().Contain(CSharpPatternGroup.ClassDeclarations);
    }

    [Test]
    [Arguments(0, CSharpPatternGroup.UsingDirectives)]
    [Arguments(1, CSharpPatternGroup.NamespaceDeclarations)]
    [Arguments(2, CSharpPatternGroup.NamespaceDeclarations)]
    [Arguments(3, CSharpPatternGroup.ClassDeclarations)]
    [Arguments(4, CSharpPatternGroup.StructDeclarations)]
    [Arguments(5, CSharpPatternGroup.RecordDeclarations)]
    [Arguments(6, CSharpPatternGroup.InterfaceDeclarations)]
    [Arguments(7, CSharpPatternGroup.EnumDeclarations)]
    [Arguments(8, CSharpPatternGroup.MethodDeclarations)]
    [Arguments(9, CSharpPatternGroup.ConstructorDeclarations)]
    [Arguments(10, CSharpPatternGroup.PropertyDeclarations)]
    [Arguments(11, CSharpPatternGroup.FieldDeclarations)]
    [Arguments(12, CSharpPatternGroup.EventDeclarations)]
    [Arguments(13, CSharpPatternGroup.EventDeclarations)]
    [Arguments(14, CSharpPatternGroup.IndexerDeclarations)]
    [Arguments(15, CSharpPatternGroup.Comments)]
    public void ClassifyPattern_MapsCorrectly(int patternIndex, CSharpPatternGroup expected)
    {
        CSharpQueries.ClassifyPattern(patternIndex).Should().Be(expected);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(16)]
    [Arguments(100)]
    public void ClassifyPattern_OutOfRange_Throws(int patternIndex)
    {
        var action = () => CSharpQueries.ClassifyPattern(patternIndex);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }
}
