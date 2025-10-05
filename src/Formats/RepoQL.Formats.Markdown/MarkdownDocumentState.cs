using System.Text.Json.Nodes;
using RepoQL.Contracts;

namespace RepoQL.Formats.Markdown;

internal sealed class MarkdownDocumentState
{
    public required MarkdownSurface Surface { get; init; }

    public required string Digest { get; init; }

    public required long Size { get; init; }

    public required SemanticMediaType MediaType { get; init; }

    public required string StoreUri { get; init; }
}

internal sealed class MarkdownSurface
{
    public required Guid DocumentId { get; init; }
    public required JsonObject DocumentProperties { get; init; }
    public required IReadOnlyList<HeadingInfo> Headings { get; init; }
    public required IReadOnlyList<LinkInfo> Links { get; init; }
    public required IReadOnlyList<CodeBlockInfo> CodeBlocks { get; init; }
}

internal sealed record HeadingInfo(Guid NodeId, Guid SpanId, int Level, string Text, string Slug, DocumentSpan Span);

internal sealed record LinkInfo(Guid NodeId, Guid SpanId, string Href, string Title, string Text, DocumentSpan Span);

internal sealed record CodeBlockInfo(Guid NodeId, Guid SpanId, string Language, bool IsFenced, int LineCount, string Info, DocumentSpan Span);
