namespace RepoQL.Grammar;

public readonly record struct TextChange(TextSpan Span, string NewText);

