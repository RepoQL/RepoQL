using System.Text.Json.Nodes;

namespace RepoQL.Formats.Markdown;

internal sealed class MarkdownSurface
{
    public required Guid DocumentId { get; init; }
    public required JsonObject DocumentProperties { get; init; }
    public required IReadOnlyList<HeadingInfo> Headings { get; init; }
    public required IReadOnlyList<LinkInfo> Links { get; init; }
    public required IReadOnlyList<CodeBlockInfo> CodeBlocks { get; init; }
}