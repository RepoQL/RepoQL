namespace RepoQL.Formats.Xlsx.Surface;

/// <summary>
/// Represents a PivotTable in an XLSX worksheet.
///
/// Purpose: PivotTables indicate data analysis and summarization. They're
/// commonly used in business spreadsheets for reporting and are relevant
/// for understanding data relationships.
///
/// Complexity: PivotTable structure requires parsing pivotTableDefinition parts.
/// </summary>
internal sealed record PivotTableInfo
{
    /// <summary>
    /// Unique identifier for this pivot table node.
    /// </summary>
    public required Guid NodeId { get; init; }

    /// <summary>
    /// Span identifier for location tracking.
    /// </summary>
    public required Guid SpanId { get; init; }

    /// <summary>
    /// PivotTable name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Source data range reference.
    /// </summary>
    public string? SourceRange { get; init; }

    /// <summary>
    /// Location of the pivot table in A1 notation.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// Row field names.
    /// </summary>
    public IReadOnlyList<string> RowFields { get; init; } = [];

    /// <summary>
    /// Column field names.
    /// </summary>
    public IReadOnlyList<string> ColumnFields { get; init; } = [];

    /// <summary>
    /// Data/value field names (with aggregation, e.g., "Sum of Amount").
    /// </summary>
    public IReadOnlyList<string> ValueFields { get; init; } = [];

    /// <summary>
    /// Filter/page field names.
    /// </summary>
    public IReadOnlyList<string> FilterFields { get; init; } = [];
}
