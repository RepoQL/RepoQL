namespace RepoQL.Grammar;

public sealed class LanguageParseOptions
{
    public bool Tolerant { get; init; } = true;
    public bool CaptureTrivia { get; init; } = true;
}