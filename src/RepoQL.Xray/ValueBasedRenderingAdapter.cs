using RepoQL.Xray.Search;

namespace RepoQL.Xray;

using Xray.Search;

/// <summary>
/// Adapts the value-based allocation results to the existing rendering decision pipeline.
/// Bridges <see cref="AllocationResult"/> from <see cref="ValueBasedDecisionEngine"/>
/// to <see cref="DecisionResult"/> used by <see cref="OutputComposer"/>.
/// </summary>
public static class ValueBasedRenderingAdapter
{
    /// <summary>
    /// Converts an allocation result to a rendering decision result.
    /// </summary>
    /// <param name="allocation">The allocation result from the value-based decision engine.</param>
    /// <param name="allResults">All search results, used for omitted item analysis.</param>
    /// <returns>A decision result compatible with the rendering pipeline.</returns>
    public static DecisionResult ToDecisionResult(
        AllocationResult allocation,
        IReadOnlyList<SearchResult> allResults)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(allResults);

        // Convert allocated items to rendering decisions
        var decisions = new List<RenderingDecision>();

        foreach (var allocatedItem in allocation.Items)
        {
            var xrayResult = ToXrayResult(allocatedItem);
            var representation = MapLevel(allocatedItem.Level);
            var estimatedTokens = EstimateTokens(xrayResult, representation);

            decisions.Add(new RenderingDecision(xrayResult, representation, estimatedTokens));
        }

        // Calculate omitted count and breakdown by type
        var omittedCount = allocation.OmittedItems.Count;
        Dictionary<string, int>? omittedByType = null;

        if (omittedCount > 0)
        {
            omittedByType = allocation.OmittedItems
                .GroupBy(item => item.SemanticType ?? "unknown")
                .OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => g.Count());
        }

        return new DecisionResult(decisions, omittedCount, omittedByType);
    }

    /// <summary>
    /// Converts an allocated item to an xray result, including nested children.
    /// </summary>
    /// <param name="allocatedItem">The allocated item to convert.</param>
    /// <returns>An xray result with all fields populated from the allocated item.</returns>
    private static XrayResult ToXrayResult(AllocatedItem allocatedItem)
    {
        ArgumentNullException.ThrowIfNull(allocatedItem);

        var item = allocatedItem.Item;

        // Convert allocated children to nested XrayResults
        IReadOnlyList<XrayResult>? childObjects = null;

        if (allocatedItem.AllocatedChildren != null && allocatedItem.AllocatedChildren.Count > 0)
        {
            var children = new List<XrayResult>();
            foreach (var child in allocatedItem.AllocatedChildren)
            {
                children.Add(ToXrayResult(child));
            }

            childObjects = children;
        }

        return new XrayResult(
            Uri: item.Uri,
            Confidence: item.Confidence,
            Kind: item.Kind,
            Headline: item.Headline,
            Structure: item.Structure,
            Snippet: item.Snippet,
            Lang: item.Lang,
            SemanticType: item.SemanticType,
            ChildObjects: childObjects
        );
    }

    /// <summary>
    /// Maps a RepresentationLevel to a Representation enum value.
    /// </summary>
    /// <param name="level">The representation level to map.</param>
    /// <returns>The corresponding representation enum value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the level is not recognized.</exception>
    private static Representation MapLevel(RepresentationLevel level)
    {
        return level switch
        {
            RepresentationLevel.Minimal => Representation.Minimal,
            RepresentationLevel.Compact => Representation.Compact,
            RepresentationLevel.Standard => Representation.Standard,
            RepresentationLevel.Rich => Representation.Rich,
            _ => throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                $"Unknown representation level: {level}")
        };
    }

    /// <summary>
    /// Estimates the token cost for an xray result at a given representation level.
    /// </summary>
    /// <param name="result">The xray result.</param>
    /// <param name="representation">The representation level.</param>
    /// <returns>The estimated number of tokens.</returns>
    private static int EstimateTokens(XrayResult result, Representation representation)
    {
        return representation switch
        {
            Representation.Minimal => EstimateMinimalTokens(result),
            Representation.Compact => EstimateCompactTokens(result),
            Representation.Standard => EstimateStandardTokens(result),
            Representation.Rich => EstimateRichTokens(result),
            _ => 50 // Default fallback
        };
    }

    private static int EstimateMinimalTokens(XrayResult result)
    {
        // Just headline, minimal context
        if (string.IsNullOrEmpty(result.Headline))
            return 5;

        return Math.Max(5, result.Headline.Length / 4);
    }

    private static int EstimateCompactTokens(XrayResult result)
    {
        // URI + headline
        var headlineTokens = string.IsNullOrEmpty(result.Headline)
            ? 10
            : Math.Max(5, result.Headline.Length / 4);

        var uriTokens = Math.Max(10, result.Uri.Length / 4) + 5;

        return headlineTokens + uriTokens;
    }

    private static int EstimateStandardTokens(XrayResult result)
    {
        // URI + headline + structure
        var headlineTokens = string.IsNullOrEmpty(result.Headline)
            ? 10
            : Math.Max(5, result.Headline.Length / 4);

        var uriTokens = Math.Max(10, result.Uri.Length / 4) + 5;

        var structureTokens = string.IsNullOrEmpty(result.Structure)
            ? 20
            : Math.Max(20, result.Structure.Length / 4);

        return headlineTokens + uriTokens + structureTokens;
    }

    private static int EstimateRichTokens(XrayResult result)
    {
        // URI + snippet (snippet is the main content)
        var uriTokens = Math.Max(10, result.Uri.Length / 4) + 5;

        var snippetTokens = string.IsNullOrEmpty(result.Snippet)
            ? 50
            : Math.Max(50, result.Snippet.Length / 4);

        return uriTokens + snippetTokens;
    }
}
