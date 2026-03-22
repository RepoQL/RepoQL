namespace RepoQL.Formats.Xlsx.Surface;

/// <summary>
/// Represents a named range (defined name) in an XLSX workbook.
///
/// Purpose: Named ranges are important for understanding spreadsheet structure,
/// especially in business spreadsheets where they often represent key data areas
/// like "TaxDeductions" or "Revenue".
///
/// Complexity: Named ranges can be workbook-scoped or worksheet-scoped.
/// </summary>
internal sealed record NamedRangeInfo
{
    /// <summary>
    /// Unique identifier for this named range node.
    /// </summary>
    public required Guid NodeId { get; init; }

    /// <summary>
    /// Span identifier for location tracking.
    /// </summary>
    public required Guid SpanId { get; init; }

    /// <summary>
    /// Name of the defined name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The reference formula (e.g., "Sheet1!$A$1:$D$50").
    /// </summary>
    public required string RefersTo { get; init; }

    /// <summary>
    /// Scope: "workbook" or the name of the worksheet if worksheet-scoped.
    /// </summary>
    public string Scope { get; init; } = "workbook";

    /// <summary>
    /// Optional comment describing the named range.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>
    /// Whether this is a hidden defined name.
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// Whether this is a built-in name (like Print_Area).
    /// </summary>
    public bool IsBuiltIn { get; init; }
}
