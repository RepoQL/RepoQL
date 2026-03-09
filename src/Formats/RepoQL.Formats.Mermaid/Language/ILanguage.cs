using RepoQL.Formats.Mermaid.Syntax;

namespace RepoQL.Formats.Mermaid.Language;

public interface ILanguage
{
    string Name { get; }

    ISyntaxTree Parse(string text, LanguageParseOptions? options = null);

    ISemanticModel? Bind(ISyntaxTree tree, LanguageBindOptions? options = null);

    string Print(ISyntaxNode node);
}
