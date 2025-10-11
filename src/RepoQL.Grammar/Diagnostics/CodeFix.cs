using RepoQL.Grammar.Core;

namespace RepoQL.Grammar.Diagnostics;

public sealed record CodeFix(string Title, IReadOnlyList<TextChange> Edits);