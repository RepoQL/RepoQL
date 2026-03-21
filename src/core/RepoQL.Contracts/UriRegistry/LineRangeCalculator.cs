namespace RepoQL.Contracts;

/// <summary>
/// Extension methods for set operations on line ranges.
///
/// Purpose: Provide union and subtract operations for line-range-based
/// pattern matching. Union merges overlapping/adjacent ranges; subtract
/// removes excluded regions from included ranges.
///
/// Complexity: Union is O(n log n) due to sorting. Subtract is O(n * m)
/// where n = included ranges, m = excluded ranges. Both are acceptable
/// for typical usage with small range counts.
/// </summary>
public static class LineRangeCalculator
{
    /// <summary>
    /// Union multiple ranges, merging overlapping and adjacent ranges.
    /// </summary>
    /// <param name="ranges">Ranges to union.</param>
    /// <returns>Merged ranges sorted by start, with no overlaps or adjacencies.</returns>
    public static IReadOnlyList<LineRange> Union(this IEnumerable<LineRange> ranges)
    {
        // Filter out empty/invalid ranges and sort by start
        var sorted = ranges
            .Where(r => r.IsValid)
            .OrderBy(r => r.Start)
            .ToList();

        if (sorted.Count == 0)
            return [];

        if (sorted.Count == 1)
            return sorted;

        var result = new List<LineRange>();
        var current = sorted[0];

        for (var i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];

            // Check if current and next overlap or are adjacent
            if (current.Overlaps(next) || current.IsAdjacentTo(next))
            {
                // Merge: extend current to include next
                current = new LineRange(
                    Math.Min(current.Start, next.Start),
                    Math.Max(current.End, next.End));
            }
            else
            {
                // No overlap/adjacency: emit current, start new
                result.Add(current);
                current = next;
            }
        }

        // Don't forget the last range
        result.Add(current);

        return result;
    }

    /// <summary>
    /// Subtract exclusions from included ranges.
    /// </summary>
    /// <param name="included">Ranges to include.</param>
    /// <param name="excluded">Ranges to exclude.</param>
    /// <returns>Remaining ranges after subtraction.</returns>
    public static IReadOnlyList<LineRange> Subtract(
        this IReadOnlyList<LineRange> included,
        IReadOnlyList<LineRange> excluded)
    {
        if (included.Count == 0)
            return [];

        if (excluded.Count == 0)
            return included;

        // Start with a working copy of included ranges
        var working = included.Where(r => r.IsValid).ToList();

        // Apply each exclusion
        foreach (var excl in excluded.Where(e => e.IsValid))
        {
            working = SubtractSingleExclusion(working, excl);
        }

        return working;
    }

    /// <summary>
    /// Subtract a single exclusion from a list of ranges.
    /// </summary>
    private static List<LineRange> SubtractSingleExclusion(
        List<LineRange> ranges,
        LineRange exclusion)
    {
        var result = new List<LineRange>();

        foreach (var range in ranges)
        {
            // No overlap - keep range unchanged
            if (!range.Overlaps(exclusion))
            {
                result.Add(range);
                continue;
            }

            // Exclusion fully contains range - drop it entirely
            if (exclusion.Contains(range))
            {
                continue;
            }

            // Range fully contains exclusion - split into two parts
            if (range.Contains(exclusion))
            {
                // Left part (before exclusion)
                if (range.Start < exclusion.Start)
                {
                    result.Add(new LineRange(range.Start, exclusion.Start - 1));
                }

                // Right part (after exclusion)
                if (range.End > exclusion.End)
                {
                    result.Add(new LineRange(exclusion.End + 1, range.End));
                }
                continue;
            }

            // Partial overlap - trim the range
            if (exclusion.Start <= range.Start)
            {
                // Exclusion overlaps start - keep end portion
                result.Add(new LineRange(exclusion.End + 1, range.End));
            }
            else
            {
                // Exclusion overlaps end - keep start portion
                result.Add(new LineRange(range.Start, exclusion.Start - 1));
            }
        }

        return result;
    }
}
