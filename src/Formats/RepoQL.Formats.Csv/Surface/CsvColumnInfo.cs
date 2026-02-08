namespace RepoQL.Formats.Csv.Surface;

/// <summary>
/// Represents analysis results for a single delimited-data column.
///
/// Purpose: Captures column name, inferred type, sample values, and token cost
/// estimate for downstream rendering and querying.
///
/// Complexity: None - simple data record populated by ColumnTypeInferrer.
/// </summary>
internal sealed record CsvColumnInfo
{
    /// <summary>
    /// Zero-based column index.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Header text, or synthetic name (column_1, column_2...) when headerless.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Inferred dominant data type.
    /// </summary>
    public required CsvColumnType DataType { get; init; }

    /// <summary>
    /// Up to five unique sample values from the column.
    /// </summary>
    public IReadOnlyList<string> SampleValues { get; init; } = [];

    /// <summary>
    /// Minimum numeric value represented as text for display.
    /// </summary>
    public string? MinValue { get; init; }

    /// <summary>
    /// Maximum numeric value represented as text for display.
    /// </summary>
    public string? MaxValue { get; init; }

    /// <summary>
    /// Number of non-empty values observed.
    /// </summary>
    public int NonEmptyCount { get; init; }

    /// <summary>
    /// Estimated token cost for this column.
    /// </summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>
/// Supported inferred data types for delimited columns.
/// </summary>
internal enum CsvColumnType
{
    Unknown,
    Varchar,
    Integer,
    Float,
    Boolean,
    Date,
    Timestamp
}
