using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class PieEntry(string labelRaw, bool labelQuoted, double value, TextSpan labelSpan, TextSpan valueSpan, TextSpan span)
    : MStmt("mmd_pie_entry", span)
{
    public string LabelRaw { get; } = labelRaw;
    public bool LabelQuoted { get; } = labelQuoted;
    public double Value { get; } = value;
    public TextSpan LabelSpan { get; } = labelSpan;
    public TextSpan ValueSpan { get; } = valueSpan;
}
