using RepoQL.Grammar.Core;
using RepoQL.Grammar.Diagnostics;

namespace RepoQL.Grammar.Syntax;

public interface ISyntaxTree
{
    ISyntaxNode Root { get; }
    string SourceText { get; }
    IReadOnlyList<Diagnostic> ParseDiagnostics { get; }
    ISyntaxTree WithChanges(params TextChange[] changes);
}