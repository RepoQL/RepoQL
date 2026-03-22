namespace RepoQL.Formats.Xlsx.Surface;

/// <summary>
/// Represents a worksheet within an XLSX workbook.
///
/// Purpose: Captures worksheet structure, detected headers, column types, and contained
/// objects (tables, charts, pivots) for graph materialization and x-ray generation.
///
/// Complexity: Header detection and column analysis are performed during loading.
/// Results are cached here for materialization.
/// </summary>
internal sealed record WorksheetInfo
{
    /// <summary>
    /// Unique identifier for this worksheet node.
    /// </summary>
    public required Guid NodeId { get; init; }

    /// <summary>
    /// Span identifier for location tracking.
    /// </summary>
    public required Guid SpanId { get; init; }

    /// <summary>
    /// Worksheet name (tab name).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Zero-based index of this sheet in the workbook.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Number of rows with data.
    /// </summary>
    public int RowCount { get; init; }

    /// <summary>
    /// Number of columns with data.
    /// </summary>
    public int ColumnCount { get; init; }

    /// <summary>
    /// Used range in A1 notation (e.g., "A1:Z100").
    /// </summary>
    public string? UsedRange { get; init; }

    /// <summary>
    /// Whether the sheet is hidden.
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// Whether a header row was detected.
    /// </summary>
    public bool HasHeaderRow { get; init; }

    /// <summary>
    /// 1-based row index of the detected header row, if any.
    /// </summary>
    public int? HeaderRowIndex { get; init; }

    /// <summary>
    /// Confidence score for header detection (0.0 to 1.0).
    /// </summary>
    public float HeaderConfidence { get; init; }

    /// <summary>
    /// Column information including detected headers and data types.
    /// </summary>
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = [];

    /// <summary>
    /// Total number of formula cells in this worksheet.
    /// </summary>
    public int FormulaCount { get; init; }

    /// <summary>
    /// Whether any SUM or aggregate formulas were detected (indicates totals).
    /// </summary>
    public bool HasTotals { get; init; }

    /// <summary>
    /// Tables defined in this worksheet.
    /// </summary>
    public IReadOnlyList<TableInfo> Tables { get; init; } = [];

    /// <summary>
    /// Charts in this worksheet.
    /// </summary>
    public IReadOnlyList<ChartInfo> Charts { get; init; } = [];

    /// <summary>
    /// Pivot tables in this worksheet.
    /// </summary>
    public IReadOnlyList<PivotTableInfo> PivotTables { get; init; } = [];
}
