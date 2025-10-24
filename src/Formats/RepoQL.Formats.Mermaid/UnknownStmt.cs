using RepoQL.Grammar.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class UnknownStmt(string raw, TextSpan span) : MStmt("mmd_unknown", span)
{
    public string Raw { get; } = raw;
}