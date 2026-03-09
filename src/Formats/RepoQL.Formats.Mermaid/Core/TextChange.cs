namespace RepoQL.Formats.Mermaid.Core;

public readonly record struct TextChange(TextSpan Span, string NewText);
