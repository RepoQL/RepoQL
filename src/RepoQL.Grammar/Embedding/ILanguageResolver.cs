using RepoQL.Grammar.Language;

namespace RepoQL.Grammar.Embedding;

public interface ILanguageResolver
{
    ILanguage? Resolve(string languageId);
}