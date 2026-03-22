using System.Text.Json.Nodes;

namespace RepoQL.Formats.Xlsx.Surface;

/// <summary>
/// Represents the complete parsed structure of an XLSX workbook.
///
/// Purpose: Acts as the container for all parsed workbook data during the
/// loading phase. This is passed to the materializer to create graph records.
///
/// Complexity: Aggregates data from multiple OpenXML parts (workbook, sheets,
/// tables, charts, defined names) into a unified model.
/// </summary>
internal sealed record WorkbookSurface
{
    /// <summary>
    /// Document node ID for this workbook.
    /// </summary>
    public required Guid DocumentId { get; init; }

    /// <summary>
    /// Document-level properties as JSON for node Props.
    /// </summary>
    public required JsonObject DocumentProperties { get; init; }

    /// <summary>
    /// All worksheets in the workbook.
    /// </summary>
    public required IReadOnlyList<WorksheetInfo> Worksheets { get; init; }

    /// <summary>
    /// All named ranges in the workbook.
    /// </summary>
    public required IReadOnlyList<NamedRangeInfo> NamedRanges { get; init; }

    // Aggregated counts for x-ray

    /// <summary>
    /// Total row count across all worksheets.
    /// </summary>
    public int TotalRows => Worksheets.Sum(w => w.RowCount);

    /// <summary>
    /// Total table count across all worksheets.
    /// </summary>
    public int TotalTables => Worksheets.Sum(w => w.Tables.Count);

    /// <summary>
    /// Total chart count across all worksheets.
    /// </summary>
    public int TotalCharts => Worksheets.Sum(w => w.Charts.Count);

    /// <summary>
    /// Total pivot table count across all worksheets.
    /// </summary>
    public int TotalPivotTables => Worksheets.Sum(w => w.PivotTables.Count);

    /// <summary>
    /// Total formula count across all worksheets.
    /// </summary>
    public int TotalFormulas => Worksheets.Sum(w => w.FormulaCount);

    /// <summary>
    /// Whether any worksheet contains formulas.
    /// </summary>
    public bool HasFormulas => Worksheets.Any(w => w.FormulaCount > 0);

    /// <summary>
    /// Whether any worksheet has SUM/aggregate formulas.
    /// </summary>
    public bool HasTotals => Worksheets.Any(w => w.HasTotals);

    /// <summary>
    /// Aggregate column type counts across all worksheets.
    /// </summary>
    public IReadOnlyDictionary<ColumnDataType, int> AggregateColumnTypes
    {
        get
        {
            var counts = new Dictionary<ColumnDataType, int>();
            foreach (var worksheet in Worksheets)
            {
                foreach (var column in worksheet.Columns)
                {
                    if (column.DataType != ColumnDataType.Unknown)
                    {
                        counts.TryGetValue(column.DataType, out var current);
                        counts[column.DataType] = current + 1;
                    }
                }
            }
            return counts;
        }
    }
}
