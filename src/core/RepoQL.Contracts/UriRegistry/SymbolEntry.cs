namespace RepoQL.Contracts;

/// <summary>
/// Symbol metadata including kind and location span.
///
/// Purpose: Store symbol information in the URI registry with span data
/// for line-range-based globbing operations.
///
/// Complexity: Simple immutable record. Span values are 1-based inclusive.
/// Zero span (0, 0) indicates span data unavailable.
/// </summary>
/// <param name="Kind">The symbol kind (e.g., "class", "method", "function", "type").</param>
/// <param name="StartLine">Start line of the symbol, 1-based inclusive. Zero if unavailable.</param>
/// <param name="EndLine">End line of the symbol, 1-based inclusive. Zero if unavailable.</param>
public record SymbolEntry(
    string Kind,
    int StartLine,
    int EndLine)
{
    /// <summary>
    /// Returns true if span data is available (non-zero).
    /// </summary>
    public bool HasSpan => StartLine > 0 && EndLine > 0;

    /// <summary>
    /// Creates a SymbolEntry with only kind (no span data).
    /// </summary>
    public static SymbolEntry WithKindOnly(string kind) => new(kind, 0, 0);
}
