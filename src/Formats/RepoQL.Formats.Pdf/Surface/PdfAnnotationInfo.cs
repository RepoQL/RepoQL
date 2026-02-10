namespace RepoQL.Formats.Pdf.Surface;

internal sealed record PdfAnnotationInfo
{
    public required string AnnotationType { get; init; }
    public required int Page { get; init; }
    public string? Content { get; init; }
    public string? Author { get; init; }
    public string? Date { get; init; }
}
