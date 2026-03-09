using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

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
