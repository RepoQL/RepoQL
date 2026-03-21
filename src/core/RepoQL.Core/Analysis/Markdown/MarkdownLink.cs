namespace RepoQL.Core.Analysis.Markdown;

internal sealed class MarkdownLink
{
    public Guid NodeId { get; init; }
    public Guid? SpanId { get; init; }
    public string? Uri { get; init; }
    public string? Href { get; init; }
    public int StartLine { get; init; }
    public int StartColumn { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }

    public string SemanticIdentifier => $"node:{NodeId}";
}
