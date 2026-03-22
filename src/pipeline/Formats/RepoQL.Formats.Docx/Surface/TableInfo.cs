namespace RepoQL.Formats.Docx.Surface;

internal sealed record TableInfo
{
    public required Guid NodeId { get; init; }
    public required Guid SpanId { get; init; }
    public required int RowCount { get; init; }
    public required int ColCount { get; init; }
    public required bool HasHeader { get; init; }
    public required IReadOnlyList<string> ColumnNames { get; init; }
    public required IReadOnlyList<IReadOnlyList<CellInfo?>> Cells { get; init; }
    public required bool IsLayout { get; init; }
    public required string Symbol { get; init; }
    public required int OutputLine { get; init; }
    public required int StartChar { get; init; }
    public required int EndChar { get; init; }
}
