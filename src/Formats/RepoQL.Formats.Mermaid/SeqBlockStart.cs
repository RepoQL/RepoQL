using RepoQL.Grammar.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class SeqBlockStart(string blockKind, string? text, TextSpan span) : MStmt("mmd_seq_block_start", span)
{
    public string BlockKind { get; } = blockKind; // alt|opt|loop
    public string? Text { get; } = text;
}