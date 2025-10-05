namespace RepoQL.Grammar;

public interface ILanguageResolver
{
    ILanguage? Resolve(string languageId);
}