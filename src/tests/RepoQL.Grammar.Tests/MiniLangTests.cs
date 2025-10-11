using System.Collections.Immutable;
using AwesomeAssertions;
using Pidgin;
using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Parsing.Pidgin;
using RepoQL.Grammar.Rules;
using RepoQL.Grammar.Runner;
using RepoQL.Grammar.Syntax;
using static Pidgin.Parser;

namespace RepoQL.Grammar.Tests;

// Minimal nodes for testing
internal sealed class Node(string kind, TextSpan span, IReadOnlyList<ISyntaxNode>? children = null, string? text = null)
    : ISyntaxNode
{
    public string Kind { get; } = kind;
    public TextSpan Span { get; } = span;
    public string? Text { get; } = text;
    private IReadOnlyList<ISyntaxNode> ChildrenList { get; } = children ?? ImmutableArray<ISyntaxNode>.Empty;
    public IEnumerable<ISyntaxNode> Children() => ChildrenList;
    public override string ToString() => Text is null ? Kind : $"{Kind}({Text})";
}

// Pidgin-based tiny language: let <id> = <int> ; (repeated)
internal sealed class MiniLang : PidginLanguageBase
{
    public override string Name => "MiniLang";

    private static Parser<char, T> Tok<T>(Parser<char, T> p) => p.Before(SkipWhitespaces);

    private static readonly Parser<char, string> Ident = Tok(
        Map(
            (h, t) => h + t,
            Letter.Or(Char('_')).Select(c => c.ToString()),
            LetterOrDigit.Or(Char('_')).ManyString()
        )
    );

    private static readonly Parser<char, string> Int = Tok(Digit.AtLeastOnceString());
    private static readonly Parser<char, string> KwLet = Tok(String("let"));
    private static readonly Parser<char, char> Eq = Tok(Char('='));
    private static readonly Parser<char, char> Semi = Tok(Char(';'));

    private static Parser<char, ISyntaxNode> LetDecl =>
        KwLet.Then(Ident, (_, id) => id)
             .Before(Eq)
             .Then(Int, (id, num) => (ISyntaxNode)new Node("LetDecl", new TextSpan(0, 0),
             [
                 new Node("Identifier", new TextSpan(0, 0), text: id),
                     new Node("Literal",    new TextSpan(0, 0), text: num)
             ]))
             .Before(Semi);

    private static Parser<char, ISyntaxNode> Program =>
        LetDecl.Many()
               .Select(list => (ISyntaxNode)new Node("Program", new TextSpan(0, 0), list.ToList()));

    protected override Parser<char, ISyntaxNode> Root => SkipWhitespaces.Then(Program).Before(SkipWhitespaces);
}

internal sealed class DuplicateVarRule : IRule
{
    public DiagnosticId Id => new("mini/duplicate-var");
    public string Title => "Duplicate variable";
    public string Description => "Variable declared more than once";
    public Severity DefaultSeverity => Severity.Error;

    public IEnumerable<Diagnostic> Analyze(RuleContext ctx)
    {
        var text = ctx.Tree.SourceText;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decl in ctx.Tree.Root.Children().Where(n => n.Kind == "LetDecl"))
        {
            var id = decl.Children().FirstOrDefault(n => n.Kind == "Identifier");
            if (id is null) continue;
            var name = (id as Node)?.Text ?? (id.Span.Length > 0 ? text.Substring(id.Span.Start, id.Span.Length) : string.Empty);
            if (!seen.Add(name))
            {
                yield return new Diagnostic(Id, Severity.Error, $"Duplicate '{name}'", id.Span, []);
            }
        }
    }
}

internal sealed class RuleSet(params IRule[] rules) : IRuleSet
{
    public IReadOnlyList<IRule> Rules { get; } = rules;
}

internal class MiniLangTests
{
    [Test]
    public Task DuplicateVar_IsFlagged()
    {
        var src = "let x = 1;\nlet x = 2;\n";
        var lang = new MiniLang();
        var rules = new RuleSet(new DuplicateVarRule());
        var diags = LintRunner.LintFile(lang, src, "mem://mini", rules).ToList();

        diags.Count.Should().Be(1);
        diags[0].Message.Should().Contain("Duplicate 'x'");
        return Task.CompletedTask;
    }
}
