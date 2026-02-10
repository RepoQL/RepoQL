using RepoQL.Formats.Pdf.TextExtraction;

namespace RepoQL.Formats.Pdf.Surface;

internal sealed record PdfDocumentSurface
{
    public required Guid DocumentId { get; init; }
    public required PdfDocumentMetadata Metadata { get; init; }
    public required IReadOnlyList<PageInfo> Pages { get; init; }
    public required IReadOnlyList<BookmarkInfo> Bookmarks { get; init; }
    public required IReadOnlyList<FormFieldInfo> FormFields { get; init; }
    public required IReadOnlyList<PdfAnnotationInfo> PdfAnnotations { get; init; }
    public required IReadOnlyList<PdfLinkInfo> Links { get; init; }
    public required IReadOnlyList<string> EmbeddedFileNames { get; init; }
    public required IReadOnlyList<string> PageTexts { get; init; }
    public required PageAssemblyResult AssembledText { get; init; }
    public required PdfDocumentStats Stats { get; init; }
}

internal sealed record PdfDocumentMetadata
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
    public string? Producer { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? Version { get; init; }
}

internal sealed record PdfDocumentStats
{
    public required int PageCount { get; init; }
    public required int TextPageCount { get; init; }
    public required int ImageOnlyPageCount { get; init; }
    public required bool HasBookmarks { get; init; }
    public required int BookmarkCount { get; init; }
    public required bool HasForm { get; init; }
    public required int FormFieldCount { get; init; }
    public required bool HasValues { get; init; }
    public required int AnnotationCount { get; init; }
    public required int LinkCount { get; init; }
    public required int ImageCount { get; init; }
    public required int PagesWithImages { get; init; }
    public required int EmbeddedFileCount { get; init; }
}
