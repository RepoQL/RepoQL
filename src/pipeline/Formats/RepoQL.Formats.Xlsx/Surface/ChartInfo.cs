namespace RepoQL.Formats.Xlsx.Surface;

/// <summary>
/// Represents a chart in an XLSX worksheet.
///
/// Purpose: Charts indicate data visualization and are often found in summary
/// sheets. Knowing chart presence and type helps understand spreadsheet purpose.
///
/// Complexity: Chart type detection requires parsing DrawingML structures.
/// </summary>
internal sealed record ChartInfo
{
    /// <summary>
    /// Unique identifier for this chart node.
    /// </summary>
    public required Guid NodeId { get; init; }

    /// <summary>
    /// Span identifier for location tracking.
    /// </summary>
    public required Guid SpanId { get; init; }

    /// <summary>
    /// Chart name or identifier.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Chart title text, if any.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Type of chart (bar, line, pie, etc.).
    /// </summary>
    public required string ChartType { get; init; }

    /// <summary>
    /// Number of data series in the chart.
    /// </summary>
    public int SeriesCount { get; init; }

    /// <summary>
    /// Data range reference, if determinable.
    /// </summary>
    public string? DataRange { get; init; }

    /// <summary>
    /// Whether the chart has a legend.
    /// </summary>
    public bool HasLegend { get; init; }
}
