namespace RepoQL.Formats.Xlsx.Surface;

/// <summary>
/// Represents an Excel table (ListObject) within a worksheet.
///
/// Purpose: Captures table structure for graph materialization. Tables are named
/// ranges with structured column headers and optional totals rows.
///
/// Complexity: Table columns have explicit headers defined by Excel, unlike
/// detected headers in regular ranges.
/// </summary>
internal sealed record TableInfo
{
    /// <summary>
    /// Unique identifier for this table node.
    /// </summary>
    public required Guid NodeId { get; init; }

    /// <summary>
    /// Span identifier for location tracking.
    /// </summary>
    public required Guid SpanId { get; init; }

    /// <summary>
    /// Internal table name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Display name for the table.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Range reference in A1 notation (e.g., "A1:F100").
    /// </summary>
    public required string Range { get; init; }

    /// <summary>
    /// Number of data rows (excluding header and totals).
    /// </summary>
    public int RowCount { get; init; }

    /// <summary>
    /// Number of columns.
    /// </summary>
    public int ColumnCount { get; init; }

    /// <summary>
    /// Whether the table has a header row.
    /// </summary>
    public bool HasHeaderRow { get; init; }

    /// <summary>
    /// Whether the table has a totals row.
    /// </summary>
    public bool HasTotalsRow { get; init; }

    /// <summary>
    /// Table column definitions with names and data types.
    /// </summary>
    public IReadOnlyList<TableColumnInfo> Columns { get; init; } = [];
}

/// <summary>
/// Represents a column within an Excel table.
/// </summary>
internal sealed record TableColumnInfo
{
    /// <summary>
    /// Column name from the table header.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Inferred data type for this column.
    /// </summary>
    public ColumnDataType DataType { get; init; } = ColumnDataType.Unknown;

    /// <summary>
    /// Formula for calculated columns, if any.
    /// </summary>
    public string? CalculatedFormula { get; init; }

    /// <summary>
    /// Totals row function (Sum, Average, Count, etc.), if any.
    /// </summary>
    public string? TotalsFunction { get; init; }
}
