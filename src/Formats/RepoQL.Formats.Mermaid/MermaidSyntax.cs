using RepoQL.Grammar.Core;
using RepoQL.Grammar.Syntax;

namespace RepoQL.Formats.Mermaid;

public abstract class MNode(string kind, TextSpan span) : ISyntaxNode
{
    public string Kind { get; } = kind;
    public TextSpan Span { get; } = span;
    public virtual IEnumerable<ISyntaxNode> Children() { yield break; }
}

public abstract class MStmt(string sKind, TextSpan span) : MNode(sKind, span);

public sealed class MDocument(string diagramKind, IReadOnlyList<MStmt> statements, TextSpan span)
    : MNode("mmd_document", span)
{
    public string DiagramKind { get; } = diagramKind;
    public IReadOnlyList<MStmt> Statements { get; } = statements;
    public override IEnumerable<ISyntaxNode> Children() => Statements;
}

public sealed class FlowNodeDecl(
    string id,
    char shapeOpen,
    char shapeClose,
    string label,
    bool labelQuoted,
    bool isClosed,
    TextSpan idSpan,
    TextSpan labelSpan,
    TextSpan span)
    : MStmt("mmd_flow_node", span)
{
    public string Id { get; } = id;
    public char ShapeOpen { get; } = shapeOpen;
    public char ShapeClose { get; } = shapeClose;
    public string Label { get; } = label;
    public bool LabelQuoted { get; } = labelQuoted;
    public bool IsClosed { get; } = isClosed;
    public TextSpan IdSpan { get; } = idSpan;
    public TextSpan LabelSpan { get; } = labelSpan;
}

public sealed class FlowEdge(
    string src,
    string arrow,
    string? midLabel,
    string dst,
    TextSpan srcSpan,
    TextSpan arrowSpan,
    TextSpan? midLabelSpan,
    TextSpan dstSpan,
    TextSpan span)
    : MStmt("mmd_flow_edge", span)
{
    public string Src { get; } = src;
    public string Arrow { get; } = arrow;
    public string? MidLabel { get; } = midLabel;
    public string Dst { get; } = dst;
    public TextSpan SrcSpan { get; } = srcSpan;
    public TextSpan ArrowSpan { get; } = arrowSpan;
    public TextSpan? MidLabelSpan { get; } = midLabelSpan;
    public TextSpan DstSpan { get; } = dstSpan;
}

public sealed class SeqParticipant(string keyword, string name, string? alias, TextSpan nameSpan, TextSpan span)
    : MStmt("mmd_seq_participant", span)
{
    public string Keyword { get; } = keyword;
    public string Name { get; } = name;
    public string? Alias { get; } = alias;
    public TextSpan NameSpan { get; } = nameSpan;
}

public sealed class SeqMessage(string from, string arrow, string to, string text, TextSpan textSpan, TextSpan span)
    : MStmt("mmd_seq_message", span)
{
    public string From { get; } = from;
    public string Arrow { get; } = arrow;
    public string To { get; } = to;
    public string Text { get; } = text;
    public TextSpan TextSpan { get; } = textSpan;
}

public sealed class PieEntry(string labelRaw, bool labelQuoted, double value, TextSpan labelSpan, TextSpan valueSpan, TextSpan span)
    : MStmt("mmd_pie_entry", span)
{
    public string LabelRaw { get; } = labelRaw;
    public bool LabelQuoted { get; } = labelQuoted;
    public double Value { get; } = value;
    public TextSpan LabelSpan { get; } = labelSpan;
    public TextSpan ValueSpan { get; } = valueSpan;
}

public sealed class UnknownStmt(string raw, TextSpan span) : MStmt("mmd_unknown", span)
{
    public string Raw { get; } = raw;
}

// Flowchart extras
public sealed class FlowSubgraphStart(string name, TextSpan span) : MStmt("mmd_flow_subgraph_start", span)
{
    public string Name { get; } = name;
}

public sealed class FlowEnd(TextSpan span) : MStmt("mmd_flow_end", span)
{ }

public sealed class ClassDef(string name, string attributes, TextSpan span) : MStmt("mmd_classdef", span)
{
    public string Name { get; } = name;
    public string Attributes { get; } = attributes;
}

public sealed class ClickStmt(string nodeId, string? href, string? tooltip, string? link, TextSpan span) : MStmt("mmd_click", span)
{
    public string NodeId { get; } = nodeId;
    public string? Href { get; } = href;
    public string? Tooltip { get; } = tooltip;
    public string? Link { get; } = link;
}

// Sequence blocks
public sealed class SeqBlockStart(string blockKind, string? text, TextSpan span) : MStmt("mmd_seq_block_start", span)
{
    public string BlockKind { get; } = blockKind; // alt|opt|loop
    public string? Text { get; } = text;
}

public sealed class SeqEnd(TextSpan span) : MStmt("mmd_seq_end", span)
{ }
