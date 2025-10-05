namespace RepoQL.Grammar;

public sealed record CodeFix(string Title, IReadOnlyList<TextChange> Edits);