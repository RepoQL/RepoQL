using RepoQL.Formats.Mermaid.Core;

namespace RepoQL.Formats.Mermaid;

public sealed class ClickStmt(string nodeId, string? href, string? tooltip, string? link, TextSpan span) : MStmt("mmd_click", span)
{
    public string NodeId { get; } = nodeId;
    public string? Href { get; } = href;
    public string? Tooltip { get; } = tooltip;
    public string? Link { get; } = link;
}
