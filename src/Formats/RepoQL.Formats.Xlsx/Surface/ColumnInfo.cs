namespace RepoQL.Formats.Xlsx.Surface;

/// <summary>
/// Represents analysis results for a single column in a worksheet.
///
/// Purpose: Captures column metadata including header detection and data type analysis
/// for efficient discovery and querying of spreadsheet data.
///
/// Complexity: Data type inference logic in ColumnAnalyzer determines the dominant type.
/// </summary>
internal sealed record ColumnInfo
{
    /// <summary>
    /// Column letter (A, B, C, ... AA, AB, etc.)
    /// </summary>
    public required string Letter { get; init; }

    /// <summary>
    /// Zero-based column index.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Detected header text for this column, if any.
    /// </summary>
    public string? Header { get; init; }

    /// <summary>
    /// Whether this column is part of a merged header cell.
    /// </summary>
    public bool IsMergedHeader { get; init; }

    /// <summary>
    /// If merged, the letter of the master cell containing the header text.
    /// </summary>
    public string? MergedWithColumn { get; init; }

    /// <summary>
    /// Dominant data type in this column.
    /// </summary>
    public ColumnDataType DataType { get; init; } = ColumnDataType.Unknown;

    /// <summary>
    /// Count of each data type found in this column.
    /// </summary>
    public IReadOnlyDictionary<ColumnDataType, int> TypeCounts { get; init; } =
        new Dictionary<ColumnDataType, int>();

    /// <summary>
    /// How consistent the data types are (0.0 to 1.0).
    /// </summary>
    public float Homogeneity { get; init; }

    /// <summary>
    /// Whether any cells in this column contain formulas.
    /// </summary>
    public bool HasFormulas { get; init; }

    /// <summary>
    /// Number of formula cells in this column.
    /// </summary>
    public int FormulaCount { get; init; }

    /// <summary>
    /// A sample value from this column for display purposes.
    /// </summary>
    public string? SampleValue { get; init; }

    /// <summary>
    /// Multiple unique sample values for text/categorical columns (up to 5).
    /// </summary>
    public IReadOnlyList<string> SampleValues { get; init; } = [];

    /// <summary>
    /// Whether the sample value(s) came from formula cells.
    /// </summary>
    public bool SampleIsFormula { get; init; }

    /// <summary>
    /// Minimum numeric value (for numeric columns).
    /// </summary>
    public double? MinValue { get; init; }

    /// <summary>
    /// Maximum numeric value (for numeric columns).
    /// </summary>
    public double? MaxValue { get; init; }

    /// <summary>
    /// Minimum date value as Excel serial number (for date columns).
    /// </summary>
    public double? MinDate { get; init; }

    /// <summary>
    /// Maximum date value as Excel serial number (for date columns).
    /// </summary>
    public double? MaxDate { get; init; }

    /// <summary>
    /// Number of non-empty cells in this column.
    /// </summary>
    public int NonEmptyCount { get; init; }
}

/// <summary>
/// Data types that can be detected in Excel columns.
/// </summary>
public enum ColumnDataType
{
    Unknown,
    Text,
    Numeric,
    Date,
    DateTime,
    Currency,
    Percentage,
    Formula,
    Boolean,
    Mixed
}
