namespace RepoQL.Explore;

/// <summary>
/// Tracks novelty scores for search results with diminishing returns.
///
/// The novelty formula provides diminishing returns for items of the same type or from the same file.
/// Formula: novelty = 1.0 / (1.0 + 0.2 * (count - 1))
///
/// Examples:
/// - First item: 1.0
/// - Second: 0.83 (1/1.2)
/// - Third: 0.71 (1/1.4)
/// - Fourth: 0.63 (1/1.6)
/// - Fifth: 0.56 (1/1.8)
/// </summary>
public class NoveltyTracker
{
    private readonly Dictionary<string, int> _counts = new();
    private readonly Dictionary<string, int> _typeCounts = new();
    private readonly Dictionary<string, int> _fileCounts = new();

    /// <summary>
    /// Gets the novelty factor for a specific key and increments its count.
    /// </summary>
    /// <param name="key">The key to track (e.g., symbol URI, unique identifier).</param>
    /// <returns>The novelty factor between 0 and 1, where 1.0 is completely novel.</returns>
    public double GetNovelty(string key)
    {
        if (string.IsNullOrEmpty(key))
            return 1.0;

        if (!_counts.ContainsKey(key))
            _counts[key] = 0;

        _counts[key]++;
        return CalculateNovelty(_counts[key]);
    }

    /// <summary>
    /// Gets the novelty factor for a specific object type and increments its count.
    /// </summary>
    /// <param name="kind">The object type/kind (e.g., "cs_method", "ts_class", "markdown_heading").</param>
    /// <returns>The novelty factor between 0 and 1, where 1.0 is completely novel.</returns>
    public double GetNoveltyByType(string kind)
    {
        if (string.IsNullOrEmpty(kind))
            return 1.0;

        if (!_typeCounts.ContainsKey(kind))
            _typeCounts[kind] = 0;

        _typeCounts[kind]++;
        return CalculateNovelty(_typeCounts[kind]);
    }

    /// <summary>
    /// Gets the novelty factor for a specific document/file and increments its count.
    /// </summary>
    /// <param name="documentUri">The document URI (e.g., "file:///src/Foo.cs").</param>
    /// <returns>The novelty factor between 0 and 1, where 1.0 is completely novel.</returns>
    public double GetNoveltyByFile(string documentUri)
    {
        if (string.IsNullOrEmpty(documentUri))
            return 1.0;

        if (!_fileCounts.ContainsKey(documentUri))
            _fileCounts[documentUri] = 0;

        _fileCounts[documentUri]++;
        return CalculateNovelty(_fileCounts[documentUri]);
    }

    /// <summary>
    /// Gets the combined novelty factor considering both object type and file.
    /// Uses geometric mean of both factors to balance their influence.
    /// </summary>
    /// <param name="kind">The object type/kind.</param>
    /// <param name="documentUri">The document URI.</param>
    /// <returns>The combined novelty factor, calculated as the geometric mean of type and file novelties.</returns>
    public double GetCombinedNovelty(string kind, string documentUri)
    {
        if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(documentUri))
            return 1.0;

        var typeNovelty = GetNoveltyByType(kind);
        var fileNovelty = GetNoveltyByFile(documentUri);

        // Geometric mean: sqrt(typeNovelty * fileNovelty)
        return Math.Sqrt(typeNovelty * fileNovelty);
    }

    /// <summary>
    /// Resets all novelty tracking counts.
    /// </summary>
    public void Reset()
    {
        _counts.Clear();
        _typeCounts.Clear();
        _fileCounts.Clear();
    }

    /// <summary>
    /// Calculates the novelty factor based on the occurrence count.
    /// Formula: novelty = 1.0 / (1.0 + 0.2 * (count - 1))
    /// </summary>
    /// <param name="count">The occurrence count (1-based).</param>
    /// <returns>The novelty factor between 0 and 1.</returns>
    private static double CalculateNovelty(int count)
    {
        if (count <= 0)
            return 1.0;

        // Novelty formula: 1.0 / (1.0 + 0.2 * (count - 1))
        return 1.0 / (1.0 + 0.2 * (count - 1));
    }
}
