using RepoQL.Formats.Mermaid.Language;
using RepoQL.Formats.Mermaid.Syntax;

namespace RepoQL.Formats.Mermaid.Rules;

public sealed class RuleContext
{
    public required ILanguage Language { get; init; }

    public required ISyntaxTree Tree { get; init; }

    public ISemanticModel? SemanticModel { get; init; }

    public required string FilePath { get; init; }

    public required CancellationToken Cancel { get; init; }
}
