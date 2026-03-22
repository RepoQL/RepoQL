using RepoQL.Contracts;
using RepoQL.Formats.Xlsx.Surface;

namespace RepoQL.Formats.Xlsx;

/// <summary>
/// Internal state container for XLSX documents between load and materialize phases.
///
/// Purpose: Carries parsed workbook structure from LoadAsync to Materialize,
/// along with file metadata needed for artifact creation.
///
/// Complexity: None - simple data transfer object following the pattern
/// established by MarkdownDocumentState.
/// </summary>
internal sealed class XlsxDocumentState
{
    /// <summary>
    /// Parsed workbook structure.
    /// </summary>
    public required WorkbookSurface Surface { get; init; }

    /// <summary>
    /// SHA-256 digest of the file content.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Semantic media type for this document.
    /// </summary>
    public required SemanticMediaType MediaType { get; init; }

    /// <summary>
    /// Storage URI for the file.
    /// </summary>
    public required string StoreUri { get; init; }
}
