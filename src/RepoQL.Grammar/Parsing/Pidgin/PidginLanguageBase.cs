using Pidgin;
using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;
using RepoQL.Grammar.Language;
using RepoQL.Grammar.Syntax;
using static Pidgin.Parser<char>;

namespace RepoQL.Grammar.Parsing.Pidgin;

/// <summary>
/// Base class for Pidgin-powered languages. Implement <see cref="Root"/> and return an <see cref="ISyntaxNode"/> tree.
/// </summary>
public abstract class PidginLanguageBase : ILanguage
{
    public abstract string Name { get; }
    protected abstract Parser<char, ISyntaxNode> Root { get; }

    public ISyntaxTree Parse(string text, LanguageParseOptions? options = null)
        => new PidginTree(text, Root);

    public virtual ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? options = null) => null;
    public virtual string Print(ISyntaxNode? node) => node?.ToString() ?? string.Empty;

    private sealed class PidginTree : ISyntaxTree
    {
        private readonly Parser<char, ISyntaxNode> _root;
        public ISyntaxNode Root { get; }
        public string SourceText { get; }
        public IReadOnlyList<Diagnostic> ParseDiagnostics { get; }

        public PidginTree(string text, Parser<char, ISyntaxNode> root)
        {
            _root = root;
            SourceText = text;
            try
            {
                Root = root.Before(End).ParseOrThrow(text);
                ParseDiagnostics = [];
            }
            catch (Exception e)
            {
                Root = new ErrorNode(new TextSpan(0, text.Length));
                ParseDiagnostics =
                [
                    new Diagnostic(new("parse/error"), Severity.Error, e.Message, new TextSpan(0, 0), [])
                ];
            }
        }

        public ISyntaxTree WithChanges(params TextChange[] changes)
        {
            if (changes.Length == 0) return this;
            var ordered = changes.OrderByDescending(c => c.Span.Start);
            var sb = new System.Text.StringBuilder(SourceText);
            foreach (var c in ordered)
            {
                sb.Remove(c.Span.Start, c.Span.Length);
                sb.Insert(c.Span.Start, c.NewText);
            }
            return new PidginTree(sb.ToString(), _root);
        }

        private sealed class ErrorNode(TextSpan span) : ISyntaxNode
        {
            public string Kind => "Error";
            public TextSpan Span { get; } = span;
            public IEnumerable<ISyntaxNode> Children() { yield break; }
        }
    }
}
