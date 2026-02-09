namespace RepoQL.Formats.Docx.Surface;

internal sealed record CellInfo
{
    public required string Text { get; init; }
    public required int RowSpan { get; init; }
    public required int ColSpan { get; init; }
}
