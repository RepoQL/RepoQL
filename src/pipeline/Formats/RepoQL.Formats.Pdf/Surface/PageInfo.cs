namespace RepoQL.Formats.Pdf.Surface;

internal sealed record PageInfo
{
    public required int Number { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public int? Rotation { get; init; }
    public required bool HasText { get; init; }
    public required bool IsImageOnly { get; init; }
}
