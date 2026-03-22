using System.Text.RegularExpressions;
using RepoQL.Contracts.Search;

namespace RepoQL.Explore.Search;

/// <summary>
/// Applies regex pattern boosts and penalties to search results.
/// </summary>
public static class PatternBooster
{
    /// <summary>
    /// Each boost pattern match multiplies score by this factor (compounding).
    /// </summary>
    private const double BoostMultiplier = 1.1;

    /// <summary>
    /// Each penalize pattern match multiplies score by this factor (de-ranking).
    /// </summary>
    private const double PenalizeMultiplier = 0.5;

    /// <summary>
    /// Apply pattern boosts to search results.
    /// Each match applies 110% boost (compounding).
    /// </summary>
    public static bool ApplyBoosts(IList<SearchResult> results, IReadOnlyList<Regex> patterns)
    {
        if (patterns.Count == 0) return false;

        var changed = false;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var searchText = $"{result.Uri} {result.Symbol} {result.Headline} {result.Snippet}";
            var matchCount = CountMatches(searchText, patterns);

            if (matchCount > 0)
            {
                var boost = Math.Pow(BoostMultiplier, matchCount);
                results[i] = result with { RawScore = result.RawScore * boost };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Apply pattern boosts to object matches.
    /// </summary>
    public static void ApplyBoosts(IList<ObjectMatch> objects, IReadOnlyList<Regex> patterns)
    {
        if (patterns.Count == 0) return;

        foreach (var obj in objects)
        {
            var searchText = $"{obj.Uri} {obj.Symbol} {obj.Headline} {obj.Snippet}";
            var matchCount = CountMatches(searchText, patterns);

            if (matchCount > 0)
            {
                var boost = Math.Pow(BoostMultiplier, matchCount);
                obj.RawScore *= boost;
            }
        }
    }

    /// <summary>
    /// Apply pattern penalties to search results.
    /// Each match applies 50% penalty (de-ranking).
    /// </summary>
    public static bool ApplyPenalties(IList<SearchResult> results, IReadOnlyList<Regex> patterns)
    {
        if (patterns.Count == 0) return false;

        var changed = false;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var searchText = $"{result.Uri} {result.Symbol} {result.Headline} {result.Snippet}";
            var matchCount = CountMatches(searchText, patterns);

            if (matchCount > 0)
            {
                var penalty = Math.Pow(PenalizeMultiplier, matchCount);
                results[i] = result with { RawScore = result.RawScore * penalty };
                changed = true;
            }
        }

        return changed;
    }

    private static int CountMatches(string searchText, IReadOnlyList<Regex> patterns)
    {
        var matchCount = 0;
        foreach (var pattern in patterns)
        {
            try
            {
                if (pattern.IsMatch(searchText))
                    matchCount++;
            }
            catch (RegexMatchTimeoutException)
            {
                // Skip slow patterns
            }
        }
        return matchCount;
    }

    /// <summary>
    /// Parse comma-separated patterns into compiled regexes.
    /// Invalid patterns are skipped.
    /// </summary>
    public static IReadOnlyList<Regex> ParsePatterns(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
            return [];

        var result = new List<Regex>();
        foreach (var pattern in patterns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                result.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)));
            }
            catch (RegexParseException)
            {
                // Skip invalid patterns
            }
        }
        return result;
    }
}
