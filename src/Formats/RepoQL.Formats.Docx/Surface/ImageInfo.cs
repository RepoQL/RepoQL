namespace RepoQL.Formats.Docx.Surface;

internal sealed record ImageInfo
{
    public required Guid NodeId { get; init; }
    public required Guid SpanId { get; init; }
    public string? AltText { get; init; }
    public string? Caption { get; init; }
    public string? ContentType { get; init; }
    public required bool IsEmbedded { get; init; }
    public required bool IsMissing { get; init; }
    public required int ParagraphIndex { get; init; }
    public required int OutputLine { get; init; }
    public required int StartChar { get; init; }
    public required int EndChar { get; init; }
}
