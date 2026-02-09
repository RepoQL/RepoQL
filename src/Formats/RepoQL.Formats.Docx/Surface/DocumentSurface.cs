namespace RepoQL.Formats.Docx.Surface;

internal sealed record DocumentSurface
{
    public required Guid DocumentId { get; init; }
    public required DocumentProperties Properties { get; init; }
    public required IReadOnlyList<HeadingInfo> Headings { get; init; }
    public required IReadOnlyList<TableInfo> Tables { get; init; }
    public required IReadOnlyList<ImageInfo> Images { get; init; }
    public required IReadOnlyList<CommentInfo> Comments { get; init; }
    public required IReadOnlyList<FootnoteInfo> Footnotes { get; init; }
    public required IReadOnlyList<EndnoteInfo> Endnotes { get; init; }
    public required IReadOnlyList<HyperlinkInfo> Hyperlinks { get; init; }
    public string? HeaderText { get; init; }
    public string? FooterText { get; init; }
    public required string BodyText { get; init; }
    public required DocumentStats Stats { get; init; }
    public required bool HasTrackedChanges { get; init; }
    public required int TrackedChangeCount { get; init; }
    public required IReadOnlyList<string> TrackedChangeAuthors { get; init; }
    public required int ContentControlCount { get; init; }
    public int OpenCommentCount => Comments.Count(comment => !comment.Resolved);
    public int FormFieldCount => ContentControlCount;
}
