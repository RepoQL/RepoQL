namespace RepoQL.Grammar.Core;

public readonly record struct TextChange(TextSpan Span, string NewText);

