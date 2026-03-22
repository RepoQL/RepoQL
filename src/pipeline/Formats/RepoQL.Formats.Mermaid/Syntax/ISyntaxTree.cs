using RepoQL.Formats.Mermaid.Core;
using RepoQL.Formats.Mermaid.Diagnostics;

namespace RepoQL.Formats.Mermaid.Syntax;

public interface ISyntaxTree
{
    ISyntaxNode Root { get; }

    string SourceText { get; }

    IReadOnlyList<Diagnostic> ParseDiagnostics { get; }

    ISyntaxTree WithChanges(params TextChange[] changes);
}
