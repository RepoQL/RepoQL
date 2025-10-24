using RepoQL.Grammar.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class SeqMessage(string from, string arrow, string to, string text, TextSpan textSpan, TextSpan span)
    : MStmt("mmd_seq_message", span)
{
    public string From { get; } = from;
    public string Arrow { get; } = arrow;
    public string To { get; } = to;
    public string Text { get; } = text;
    public TextSpan TextSpan { get; } = textSpan;
}