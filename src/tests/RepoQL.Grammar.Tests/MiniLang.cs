using Pidgin;
using RepoQL.Grammar.Core;
using RepoQL.Grammar.Parsing.Pidgin;
using RepoQL.Grammar.Syntax;

namespace RepoQL.Grammar.Tests;

internal sealed class MiniLang : PidginLanguageBase
{
    public override string Name => "MiniLang";

    private static Parser<char, T> Tok<T>(Parser<char, T> p) => p.Before(Parser.SkipWhitespaces);

    private static readonly Parser<char, string> Ident = Tok(
        Parser.Map(
            (h, t) => h + t,
            Parser.Letter.Or(Parser.Char('_')).Select(c => c.ToString()),
            Parser.LetterOrDigit.Or(Parser.Char('_')).ManyString()
        )
    );

    private static readonly Parser<char, string> Int = Tok(Parser.Digit.AtLeastOnceString());
    private static readonly Parser<char, string> KwLet = Tok(Parser.String("let"));
    private static readonly Parser<char, char> Eq = Tok(Parser.Char('='));
    private static readonly Parser<char, char> Semi = Tok(Parser.Char(';'));

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

    protected override Parser<char, ISyntaxNode> Root => Parser.SkipWhitespaces.Then(Program).Before(Parser.SkipWhitespaces);
}