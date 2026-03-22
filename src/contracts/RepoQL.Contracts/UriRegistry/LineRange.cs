namespace RepoQL.Contracts;

/// <summary>
/// A contiguous range of lines within a file.
///
/// Purpose: Represent line ranges for set operations in pattern matching.
/// Used to normalize files, symbols, and explicit ranges to a common format.
///
/// Complexity: Simple value type with helper methods for range comparisons.
/// All values are 1-based inclusive (line 1 to line N means lines 1 through N).
/// </summary>
/// <param name="Start">Start line, 1-based inclusive.</param>
/// <param name="End">End line, 1-based inclusive.</param>
public readonly record struct LineRange(int Start, int End)
{
    /// <summary>
    /// Returns true if this range overlaps with another range.
    /// Adjacent ranges (e.g., [1,10] and [11,20]) do not overlap.
    /// </summary>
    public bool Overlaps(LineRange other) =>
        Start <= other.End && other.Start <= End;

    /// <summary>
    /// Returns true if this range fully contains another range.
    /// </summary>
    public bool Contains(LineRange other) =>
        Start <= other.Start && other.End <= End;

    /// <summary>
    /// Returns true if this range is adjacent to another (can be merged).
    /// </summary>
    public bool IsAdjacentTo(LineRange other) =>
        End + 1 == other.Start || other.End + 1 == Start;

    /// <summary>
    /// Returns the number of lines in this range.
    /// </summary>
    public int Length => IsEmpty ? 0 : End - Start + 1;

    /// <summary>
    /// Returns true if this is an empty or invalid range (Start > End).
    /// </summary>
    public bool IsEmpty => Start > End || Start <= 0;

    /// <summary>
    /// Returns true if this range represents valid line numbers.
    /// </summary>
    public bool IsValid => Start > 0 && End >= Start;

    /// <summary>
    /// An empty range that represents no lines.
    /// </summary>
    public static LineRange Empty => new(0, 0);

    /// <summary>
    /// Sentinel value representing the entire file when line count is unknown.
    /// Simplifier treats this as "return file URI directly".
    /// </summary>
    public static LineRange WholeFileUnknown => new(1, int.MaxValue);

    /// <summary>
    /// Returns true if this range represents "whole file with unknown size".
    /// </summary>
    public bool IsWholeFileUnknown => Start == 1 && End == int.MaxValue;

    /// <summary>
    /// Creates a range representing the entire file given its line count.
    /// Returns WholeFileUnknown if line count is 0 (unknown).
    /// </summary>
    public static LineRange WholeFile(int lineCount) =>
        lineCount > 0 ? new(1, lineCount) : WholeFileUnknown;

    /// <summary>
    /// Creates a range for a single line.
    /// </summary>
    public static LineRange SingleLine(int line) =>
        line > 0 ? new(line, line) : Empty;

    public override string ToString() =>
        IsEmpty ? "[]" : $"[{Start},{End}]";
}
