namespace RepoQL.Explore;

/// <summary>
/// Context for generating next-action hints based on what was omitted from results.
/// </summary>
/// <param name="OmittedDocuments">Number of documents not shown.</param>
/// <param name="OmittedObjects">Number of objects (symbols, headings) not shown.</param>
/// <param name="OmittedByType">Breakdown of omitted results by semantic type.</param>
/// <param name="Intent">The current intent (Explore, Find, Read).</param>
/// <param name="HasQuestion">Whether a search question was provided.</param>
/// <param name="HasScope">Whether a scope filter was used.</param>
/// <param name="Limit">The limit that was applied to results.</param>
/// <param name="TotalResults">Total results before limiting.</param>
public record FooterContext(
    int OmittedDocuments,
    int OmittedObjects,
    IReadOnlyDictionary<string, int>? OmittedByType,
    Intent Intent,
    bool HasQuestion,
    bool HasScope,
    int Limit,
    int TotalResults
);

/// <summary>
/// Generates helpful hints to guide users in refining their search.
/// Suggests actions based on what was omitted and search characteristics.
/// </summary>
public static class NextActionHintGenerator
{
    /// <summary>
    /// Generate up to 3 actionable hints based on the footer context.
    /// Hints are short, actionable suggestions (5 words or less each).
    /// Prioritizes the most relevant hints for the user's current context.
    /// </summary>
    /// <param name="context">The footer context containing omission and search details.</param>
    /// <returns>An ordered list of up to 3 hints. Empty if no hints apply.</returns>
    public static IReadOnlyList<string> GenerateHints(FooterContext context)
    {
        var hints = new List<string>();

        // Hint 1: Add scope filter when many documents are omitted and no scope exists
        if (context.OmittedDocuments > 10 && !context.HasScope)
        {
            hints.Add("add scope filter");
        }

        // Hint 2: Narrow with pattern when many objects are omitted
        if (context.OmittedObjects > 20)
        {
            hints.Add("narrow with pattern");
        }

        // Hint 3: Try Find intent when in Explore with a question (indicates specific search)
        if (context.Intent == Intent.Inventory && context.HasQuestion)
        {
            hints.Add("try Find intent");
        }

        // Hint 4: Try Read intent when in Find and code types are omitted
        if (context.Intent == Intent.Locate && HasCodeOmitted(context.OmittedByType))
        {
            hints.Add("try Read intent");
        }

        // Hint 5: Increase limit when limit is significantly constraining results
        if (context.Limit > 0 && context.TotalResults > 0)
        {
            // If limit is less than 30% of total results, it's likely the constraint
            var limitRatio = (double)context.Limit / context.TotalResults;
            if (limitRatio < 0.3)
            {
                hints.Add("increase limit");
            }
        }

        // Return at most 3 hints, prioritized
        return hints.Take(3).ToList();
    }

    /// <summary>
    /// Determines if code-related content was omitted.
    /// Checks for common code semantic types.
    /// </summary>
    private static bool HasCodeOmitted(IReadOnlyDictionary<string, int>? omittedByType)
    {
        if (omittedByType == null || omittedByType.Count == 0)
            return false;

        // Check for code-related semantic types
        var codePatterns = new[] { "code", "csharp", "typescript", "javascript", "python", "java", "rust", "go" };

        return omittedByType.Keys.Any(key =>
            codePatterns.Any(pattern => key.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
    }
}
