namespace RepoQL.Contracts;

/// <summary>
/// LINQ extension methods for URI pattern filtering.
///
/// Purpose: Provides fluent API for filtering URI collections using pattern
/// specifications. Wraps UriPatternMatcher.Matches for ease of use.
///
/// Complexity: Minimal - delegates all pattern matching to UriPatternMatcher.
/// </summary>
public static class UriPatternMatcherExtensions
{
    /// <summary>
    /// Filters URIs matching the pattern specification.
    /// </summary>
    /// <param name="uris">The URIs to filter.</param>
    /// <param name="patternSpec">Pattern specification (semicolon-delimited, ! for negatives).</param>
    /// <param name="ignoreCase">Whether to ignore case (default true).</param>
    /// <param name="defaultScheme">Default scheme for patterns without one (default file:///).</param>
    /// <returns>URIs matching the pattern specification.</returns>
    public static IEnumerable<string> MatchingGlob(
        this IEnumerable<string> uris,
        string? patternSpec,
        bool ignoreCase = true,
        string defaultScheme = "file:///")
    {
        // Blank pattern = match everything (pass through)
        if (string.IsNullOrWhiteSpace(patternSpec))
        {
            foreach (var uri in uris)
                yield return uri;
            yield break;
        }

        foreach (var uri in uris)
        {
            if (UriPatternMatcher.Matches(uri, patternSpec, ignoreCase, defaultScheme) == true)
                yield return uri;
        }
    }

    /// <summary>
    /// Filters URIs NOT matching the pattern specification (inverse of MatchingGlob).
    /// </summary>
    /// <param name="uris">The URIs to filter.</param>
    /// <param name="patternSpec">Pattern specification (semicolon-delimited, ! for negatives).</param>
    /// <param name="ignoreCase">Whether to ignore case (default true).</param>
    /// <param name="defaultScheme">Default scheme for patterns without one (default file:///).</param>
    /// <returns>URIs NOT matching the pattern specification.</returns>
    public static IEnumerable<string> NotMatchingGlob(
        this IEnumerable<string> uris,
        string? patternSpec,
        bool ignoreCase = true,
        string defaultScheme = "file:///")
    {
        // Blank pattern = match everything, so inverse returns nothing
        if (string.IsNullOrWhiteSpace(patternSpec))
            yield break;

        foreach (var uri in uris)
        {
            if (UriPatternMatcher.Matches(uri, patternSpec, ignoreCase, defaultScheme) != true)
                yield return uri;
        }
    }
}
