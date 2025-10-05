namespace RepoQL.Grammar;

public interface ISyntaxTree
{
    ISyntaxNode Root { get; }
    string SourceText { get; }
    IReadOnlyList<Diagnostic> ParseDiagnostics { get; }
    ISyntaxTree WithChanges(params TextChange[] changes);
}