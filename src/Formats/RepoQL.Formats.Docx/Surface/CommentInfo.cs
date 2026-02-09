namespace RepoQL.Formats.Docx.Surface;

internal sealed record CommentInfo
{
    public required Guid NodeId { get; init; }
    public required string Id { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? Date { get; init; }
    public required string Text { get; init; }
    public int? AnchorStartParagraph { get; init; }
    public int? AnchorEndParagraph { get; init; }
    public required bool Resolved { get; init; }
}
