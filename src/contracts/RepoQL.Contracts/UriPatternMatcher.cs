namespace RepoQL.Contracts;

/// <summary>
/// Static predicate for URI pattern matching. Supports semicolon-delimited
/// patterns, negative patterns with ! prefix, and Git-style globs.
///
/// Purpose: Single-responsibility pattern matching logic reusable across
/// LINQ, UDFs, and direct invocation.
///
/// Complexity: Pattern parsing (positive/negative) and evaluation order.
/// Protected by immutable parsed state and simple bool? return type.
/// </summary>
public static class UriPatternMatcher
{
    /// <summary>
    /// Tests if URI matches pattern specification.
    /// Returns null if URI is null/blank (SQL three-valued logic).
    /// </summary>
    /// <param name="uri">The URI to test.</param>
    /// <param name="patternSpec">Pattern specification (semicolon-delimited, ! for negatives).</param>
    /// <param name="ignoreCase">Whether to ignore case (default true).</param>
    /// <param name="defaultScheme">Default scheme for patterns without one (default file:///).</param>
    /// <returns>true if matches, false if not, null if URI is blank.</returns>
    public static bool? Matches(
        string? uri,
        string? patternSpec,
        bool ignoreCase = true,
        string defaultScheme = "file:///")
    {
        // Three-valued logic: null input = null output
        if (string.IsNullOrWhiteSpace(uri))
            return null;

        // Blank pattern = match everything
        if (string.IsNullOrWhiteSpace(patternSpec))
            return true;

        var (positives, negatives) = ParsePatterns(patternSpec);

        // Case 1: Only negative patterns - match unless ANY negative matches
        if (positives.Length == 0)
        {
            foreach (var neg in negatives)
            {
                if (RepoUriGlobMatcher.IsMatch(uri, neg, ignoreCase, defaultScheme) == true)
                    return false;
            }
            return true;
        }

        // Case 2: Has positive patterns - must match at least one positive
        var matchedPositive = false;
        foreach (var pos in positives)
        {
            if (RepoUriGlobMatcher.IsMatch(uri, pos, ignoreCase, defaultScheme) == true)
            {
                matchedPositive = true;
                break;
            }
        }

        if (!matchedPositive)
            return false;

        // Check negatives: if matches any negative, exclude
        foreach (var neg in negatives)
        {
            if (RepoUriGlobMatcher.IsMatch(uri, neg, ignoreCase, defaultScheme) == true)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Parses pattern spec into positive and negative patterns.
    /// Returns empty arrays for blank input.
    /// </summary>
    /// <param name="patternSpec">Pattern specification (semicolon-delimited, ! for negatives).</param>
    /// <returns>Tuple of (positive patterns, negative patterns).</returns>
    public static (string[] Positive, string[] Negative) ParsePatterns(string? patternSpec)
    {
        if (string.IsNullOrWhiteSpace(patternSpec))
            return ([], []);

        var parts = patternSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var positives = new List<string>();
        var negatives = new List<string>();

        foreach (var part in parts)
        {
            if (part.StartsWith('!'))
            {
                var pattern = part[1..].Trim();
                if (!string.IsNullOrWhiteSpace(pattern))
                    negatives.Add(pattern);
            }
            else if (!string.IsNullOrWhiteSpace(part))
            {
                positives.Add(part);
            }
        }

        return (positives.ToArray(), negatives.ToArray());
    }
}
