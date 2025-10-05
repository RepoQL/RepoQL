using System.Diagnostics.CodeAnalysis;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;

namespace RepoQL.Grammar;

/// <summary>
/// Base class for ANTLR4-powered languages. Implement ParseRoot and Convert to map the parse tree to ISyntaxNode.
/// </summary>
public abstract class AntlrLanguageBase<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TLexer,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TParser,
    TRoot> : ILanguage
    where TLexer  : Lexer
    where TParser : Parser
    where TRoot   : IParseTree
{
    public abstract string Name { get; }
    protected abstract TRoot ParseRoot(TParser parser);
    protected abstract ISyntaxNode Convert(TRoot tree, string text, out IReadOnlyList<Diagnostic> parseDiagnostics);

    public ISyntaxTree Parse(string text, LanguageParseOptions? options = null)
    {
        var input = new AntlrInputStream(text);
        var lexer = (TLexer)Activator.CreateInstance(typeof(TLexer), input)!;
        var tokens = new CommonTokenStream(lexer);
        var parser = (TParser)Activator.CreateInstance(typeof(TParser), tokens)!;

        TRoot root;
        IReadOnlyList<Diagnostic> diags;

        try
        {
            // Allow derived types to adjust error strategy if needed
            root = ParseRoot(parser);
            var node = Convert(root, text, out diags);
            return new Tree(text, node, diags);
        }
        catch (Exception e)
        {
            diags = new[]
            {
                new Diagnostic(new("parse/error"), Severity.Error, e.Message, new TextSpan(0, 0), Array.Empty<CodeFix>())
            };
            return new Tree(text, new ErrorNode(new TextSpan(0, text.Length)), diags);
        }
    }

    public virtual ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? options = null) => null;
    public virtual string Print(ISyntaxNode node) => node.ToString() ?? string.Empty;

    private sealed class Tree(string text, ISyntaxNode root, IReadOnlyList<Diagnostic> diags) : ISyntaxTree
    {
        public ISyntaxNode Root { get; } = root;
        public string SourceText { get; } = text;
        public IReadOnlyList<Diagnostic> ParseDiagnostics { get; } = diags;
        public ISyntaxTree WithChanges(params TextChange[] changes) => this; // leave incremental to derived types
    }

    private sealed class ErrorNode(TextSpan span) : ISyntaxNode
    {
        public string Kind => "Error";
        public TextSpan Span { get; } = span;
        public IEnumerable<ISyntaxNode> Children() { yield break; }
    }
}
