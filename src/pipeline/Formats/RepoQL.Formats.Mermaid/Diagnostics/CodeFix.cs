using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid.Diagnostics;

public sealed record CodeFix(string Title, IReadOnlyList<TextChange> Edits);
