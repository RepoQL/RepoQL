using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

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
