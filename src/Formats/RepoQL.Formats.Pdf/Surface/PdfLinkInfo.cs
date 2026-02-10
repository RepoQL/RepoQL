namespace RepoQL.Formats.Pdf.Surface;

internal sealed record PdfLinkInfo
{
    public required int Page { get; init; }
    public required string Url { get; init; }
}
