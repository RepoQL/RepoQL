using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class SeqParticipant(string keyword, string name, string? alias, TextSpan nameSpan, TextSpan span)
    : MStmt("mmd_seq_participant", span)
{
    public string Keyword { get; } = keyword;
    public string Name { get; } = name;
    public string? Alias { get; } = alias;
    public TextSpan NameSpan { get; } = nameSpan;
}
