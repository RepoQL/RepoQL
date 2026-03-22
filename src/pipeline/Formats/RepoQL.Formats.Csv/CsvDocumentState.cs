using RepoQL.Contracts;
using RepoQL.Formats.Csv.Surface;

namespace RepoQL.Formats.Csv;

/// <summary>
/// Internal state container for delimited documents between load and materialize phases.
///
/// Purpose: Carries parsed surface and file metadata across pipeline phases.
///
/// Complexity: None - data transfer object.
/// </summary>
internal sealed class CsvDocumentState
{
    /// <summary>
    /// Parsed document surface model.
    /// </summary>
    public required CsvDocumentSurface Surface { get; init; }

    /// <summary>
    /// SHA-256 digest of file content.
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
