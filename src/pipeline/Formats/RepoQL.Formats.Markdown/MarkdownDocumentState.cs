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