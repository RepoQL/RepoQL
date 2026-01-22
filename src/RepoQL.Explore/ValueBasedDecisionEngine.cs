using RepoQL.Explore.Search;

namespace RepoQL.Explore;

/// <summary>
/// Implements the greedy value-based token allocation algorithm.
/// Allocates tokens to search results based on marginal utility per token spent.
/// </summary>
/// <remarks>
/// Algorithm:
/// 1. Baseline: Include top items at Minimal level until ~40% of budget used
/// 2. Generate all possible upgrade actions (include, upgrade, reveal child)
/// 3. Greedy loop: Apply highest efficiency (benefit/cost) action until budget exhausted
/// 4. Convert final state to AllocationResult
/// </remarks>
public class ValueBasedDecisionEngine
{
    private const double BaselineBudgetFraction = 0.40; // Use ~40% of budget for baseline

    /// <summary>
    /// Allocates tokens to search results using the greedy value-based algorithm.
    /// </summary>
    /// <param name="candidates">The search results to allocate tokens to.</param>
    /// <param name="intent">The user's intent (Explore, Find, Read).</param>
    /// <param name="tokenBudget">The total token budget available.</param>
    /// <returns>An allocation result showing which items to include and at what levels.</returns>
    public AllocationResult Allocate(
        IReadOnlyList<SearchResult> candidates,
        Intent intent,
        int tokenBudget)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new AllocationResult(
                Items: Array.Empty<AllocatedItem>(),
                TokensUsed: 0,
                TokensRemaining: tokenBudget,
                OmittedItems: Array.Empty<SearchResult>());
        }

        var state = new RenderingState();
        var novelty = new NoveltyTracker();

        // Phase 1: Baseline - include top items at Minimal level
        PerformBaselineInclusion(candidates, intent, tokenBudget, state, novelty);

        // Phase 2: Greedy - repeatedly apply highest efficiency action
        PerformGreedyUpgrades(candidates, intent, tokenBudget, state, novelty);

        // Convert final state to allocation result
        return BuildAllocationResult(candidates, state, tokenBudget);
    }

    /// <summary>
    /// Phase 1: Include top items at Minimal level until ~40% of budget used.
    /// This ensures we have a baseline set of results before optimizing.
    /// </summary>
    private static void PerformBaselineInclusion(
        IReadOnlyList<SearchResult> candidates,
        Intent intent,
        int tokenBudget,
        RenderingState state,
        NoveltyTracker novelty)
    {
        var baselineBudget = (int)(tokenBudget * BaselineBudgetFraction);

        // Sort candidates by confidence (descending)
        var sortedCandidates = candidates
            .Where(c => c.Scope == SearchScope.Document) // Only documents in baseline
            .OrderByDescending(c => c.Confidence)
            .ToList();

        foreach (var candidate in sortedCandidates)
        {
            if (state.TotalTokensUsed >= baselineBudget)
                break;

            // Create include action
            var relevance = UtilityCalculator.CalculateRelevance(candidate.Confidence);
            var hasSemanticScore = candidate.Confidence >= 70;
            var hasLexicalHit = candidate.Confidence >= 40;
            var evidenceQuality = UtilityCalculator.CalculateEvidenceQuality(
                hasSemanticScore, hasLexicalHit, hasLexicalHit);

            var documentUri = candidate.Scope == SearchScope.Document
                ? candidate.Uri
                : ExtractDocumentUri(candidate.Uri);

            var itemNovelty = novelty.GetCombinedNovelty(
                candidate.Kind ?? "document",
                documentUri);

            var tokenCost = EstimateTokenCost(candidate, RepresentationLevel.Minimal);

            // Check if we have enough budget
            if (state.TotalTokensUsed + tokenCost > baselineBudget)
                break;

            var action = new IncludeItemAction(
                candidate,
                intent,
                relevance,
                evidenceQuality,
                itemNovelty,
                tokenCost);

            action.Apply(state);
        }
    }

    /// <summary>
    /// Phase 2: Greedily apply the highest efficiency action until budget exhausted.
    /// </summary>
    private static void PerformGreedyUpgrades(
        IReadOnlyList<SearchResult> candidates,
        Intent intent,
        int tokenBudget,
        RenderingState state,
        NoveltyTracker novelty)
    {
        while (true)
        {
            // Generate all possible actions given current state
            var actions = UpgradeActionGenerator.GenerateAllActions(state, candidates, novelty, intent);

            if (actions.Count == 0)
                break; // No more actions available

            // Find the action with highest efficiency (benefit / cost)
            var bestAction = actions
                .Where(a => state.TotalTokensUsed + a.TokenCost <= tokenBudget) // Only affordable actions
                .Where(a => a.Benefit > 0) // Only actions with positive benefit
                .OrderByDescending(a => a.Efficiency)
                .ThenByDescending(a => a.Benefit) // Tiebreaker: prefer higher absolute benefit
                .FirstOrDefault();

            if (bestAction == null)
                break; // No affordable actions with positive benefit

            // Apply the best action
            bestAction.Apply(state);

            // Check if we've used up the budget
            if (state.TotalTokensUsed >= tokenBudget)
                break;
        }
    }

    /// <summary>
    /// Converts the final rendering state into an AllocationResult.
    /// </summary>
    private static AllocationResult BuildAllocationResult(
        IReadOnlyList<SearchResult> candidates,
        RenderingState state,
        int tokenBudget)
    {
        var allocatedItems = new List<AllocatedItem>();
        var omittedItems = new List<SearchResult>();

        // Build allocation for each candidate
        foreach (var candidate in candidates)
        {
            var level = state.GetCurrentLevel(candidate.Uri);

            if (level != null)
            {
                // This item is included - check for allocated children
                IReadOnlyList<AllocatedItem>? allocatedChildren = null;

                if (candidate.ChildObjects != null && candidate.ChildObjects.Count > 0)
                {
                    var children = new List<AllocatedItem>();
                    foreach (var child in candidate.ChildObjects)
                    {
                        var childLevel = state.GetCurrentLevel(child.Uri);
                        if (childLevel != null && state.IsChildRevealed(child.Uri))
                        {
                            children.Add(new AllocatedItem(
                                Item: child,
                                Level: childLevel.Value,
                                AllocatedChildren: null));
                        }
                    }

                    if (children.Count > 0)
                        allocatedChildren = children;
                }

                allocatedItems.Add(new AllocatedItem(
                    Item: candidate,
                    Level: level.Value,
                    AllocatedChildren: allocatedChildren));
            }
            else
            {
                // This item was not included
                omittedItems.Add(candidate);
            }
        }

        return new AllocationResult(
            Items: allocatedItems,
            TokensUsed: state.TotalTokensUsed,
            TokensRemaining: tokenBudget - state.TotalTokensUsed,
            OmittedItems: omittedItems);
    }

    /// <summary>
    /// Estimates the token cost of including an item at a specific representation level.
    /// </summary>
    private static int EstimateTokenCost(SearchResult result, RepresentationLevel level)
    {
        return level switch
        {
            RepresentationLevel.Minimal => EstimateHeadlineTokens(result),
            RepresentationLevel.Compact => EstimateHeadlineTokens(result) + EstimateUriTokens(result),
            RepresentationLevel.Standard => EstimateHeadlineTokens(result) + EstimateUriTokens(result) + EstimateStructureTokens(result),
            RepresentationLevel.Rich => EstimateUriTokens(result) + EstimateSnippetTokens(result),
            _ => 50
        };
    }

    private static int EstimateHeadlineTokens(SearchResult result)
    {
        if (string.IsNullOrEmpty(result.Headline)) return 10;
        return Math.Max(5, result.Headline.Length / 4);
    }

    private static int EstimateUriTokens(SearchResult result)
    {
        return Math.Max(10, result.Uri.Length / 4) + 5; // URI + formatting
    }

    private static int EstimateStructureTokens(SearchResult result)
    {
        if (string.IsNullOrEmpty(result.Structure)) return 20;
        return Math.Max(20, result.Structure.Length / 4);
    }

    private static int EstimateSnippetTokens(SearchResult result)
    {
        if (string.IsNullOrEmpty(result.Snippet)) return 50;
        return Math.Max(50, result.Snippet.Length / 4);
    }

    /// <summary>
    /// Extracts the document URI from an object URI (removes fragment).
    /// For example: "file:///src/Foo.cs#line=42" → "file:///src/Foo.cs"
    /// </summary>
    private static string ExtractDocumentUri(string uri)
    {
        var hashIndex = uri.IndexOf('#', StringComparison.Ordinal);
        return hashIndex >= 0 ? uri.Substring(0, hashIndex) : uri;
    }
}

/// <summary>
/// Result of the token allocation algorithm.
/// </summary>
public record AllocationResult(
    IReadOnlyList<AllocatedItem> Items,
    int TokensUsed,
    int TokensRemaining,
    IReadOnlyList<SearchResult> OmittedItems
);

/// <summary>
/// An item with its allocated representation level and any allocated children.
/// </summary>
public record AllocatedItem(
    SearchResult Item,
    RepresentationLevel Level,
    IReadOnlyList<AllocatedItem>? AllocatedChildren
);
