namespace RepoQL.Formats.Csv.Surface;

/// <summary>
/// Represents the complete parsed structure of a delimited document.
///
/// Purpose: Container for all parsed CSV/TSV/PSV metadata between load and
/// materialize phases.
///
/// Complexity: None - simple data container.
/// </summary>
internal sealed record CsvDocumentSurface
{
    /// <summary>
    /// Document node identifier.
    /// </summary>
    public required Guid DocumentId { get; init; }

    /// <summary>
    /// Delimiter used by the document.
    /// </summary>
    public required char Delimiter { get; init; }

    /// <summary>
    /// Whether the first row was detected as a header.
    /// </summary>
    public required bool HasHeader { get; init; }

    /// <summary>
    /// Data row count excluding header.
    /// </summary>
    public required int RowCount { get; init; }

    /// <summary>
    /// Total number of columns.
    /// </summary>
    public required int ColumnCount { get; init; }

    /// <summary>
    /// Per-column analysis metadata.
    /// </summary>
    public required IReadOnlyList<CsvColumnInfo> Columns { get; init; }

    /// <summary>
    /// Total estimated token count for this document.
    /// </summary>
    public required int TotalEstimatedTokens { get; init; }
}
