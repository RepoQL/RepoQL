namespace RepoQL.Formats.Pdf.Surface;

internal sealed record BookmarkInfo
{
    public required Guid NodeId { get; init; }
    public required Guid SpanId { get; init; }
    public required string Title { get; init; }
    public required int Level { get; init; }
    public required int TargetPage { get; init; }
    public IReadOnlyList<BookmarkInfo> Children { get; init; } = [];
}
