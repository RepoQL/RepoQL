using RepoQL.Xray.Search;

namespace RepoQL.Xray;

using Xray.Search;

/// <summary>
/// Tracks what items are currently included in the rendering output and at what representation level.
/// Used to determine which upgrade actions are valid.
/// </summary>
public sealed class RenderingState
{
    private readonly Dictionary<string, RepresentationLevel> _includedItems = new();
    private readonly HashSet<string> _revealedChildren = new();
    private int _totalTokensUsed;

    /// <summary>
    /// Gets the current token usage.
    /// </summary>
    public int TotalTokensUsed => _totalTokensUsed;

    /// <summary>
    /// Checks if an item is currently included in the rendering output.
    /// </summary>
    /// <param name="uri">The URI of the item.</param>
    /// <returns>True if the item is included, false otherwise.</returns>
    public bool IsIncluded(string uri) => _includedItems.ContainsKey(uri);

    /// <summary>
    /// Gets the current representation level of an item, if it's included.
    /// </summary>
    /// <param name="uri">The URI of the item.</param>
    /// <returns>The current representation level, or null if the item is not included.</returns>
    public RepresentationLevel? GetCurrentLevel(string uri)
    {
        return _includedItems.TryGetValue(uri, out var level) ? level : null;
    }

    /// <summary>
    /// Checks if a child object has been revealed under its parent.
    /// </summary>
    /// <param name="childUri">The URI of the child object.</param>
    /// <returns>True if the child has been revealed, false otherwise.</returns>
    public bool IsChildRevealed(string childUri) => _revealedChildren.Contains(childUri);

    /// <summary>
    /// Includes a new item at the specified representation level.
    /// </summary>
    /// <param name="uri">The URI of the item.</param>
    /// <param name="level">The representation level.</param>
    /// <param name="tokenCost">The token cost of including this item.</param>
    public void Include(string uri, RepresentationLevel level, int tokenCost)
    {
        if (_includedItems.ContainsKey(uri))
            throw new InvalidOperationException($"Item {uri} is already included");

        _includedItems[uri] = level;
        _totalTokensUsed += tokenCost;
    }

    /// <summary>
    /// Upgrades an existing item to a higher representation level.
    /// </summary>
    /// <param name="uri">The URI of the item.</param>
    /// <param name="newLevel">The new representation level.</param>
    /// <param name="tokenCost">The additional token cost of the upgrade.</param>
    public void Upgrade(string uri, RepresentationLevel newLevel, int tokenCost)
    {
        if (!_includedItems.ContainsKey(uri))
            throw new InvalidOperationException($"Item {uri} is not included");

        _includedItems[uri] = newLevel;
        _totalTokensUsed += tokenCost;
    }

    /// <summary>
    /// Reveals a child object under its parent document.
    /// </summary>
    /// <param name="childUri">The URI of the child object.</param>
    /// <param name="level">The representation level for the child.</param>
    /// <param name="tokenCost">The token cost of revealing this child.</param>
    public void RevealChild(string childUri, RepresentationLevel level, int tokenCost)
    {
        if (_revealedChildren.Contains(childUri))
            throw new InvalidOperationException($"Child {childUri} has already been revealed");

        _includedItems[childUri] = level;
        _revealedChildren.Add(childUri);
        _totalTokensUsed += tokenCost;
    }

    /// <summary>
    /// Gets all included items with their current representation levels.
    /// </summary>
    public IReadOnlyDictionary<string, RepresentationLevel> IncludedItems => _includedItems;
}

/// <summary>
/// Base class for all upgrade actions in the value-based token allocation system.
/// An upgrade action represents a single atomic change to the rendering output.
/// </summary>
public abstract class UpgradeAction
{
    /// <summary>
    /// Gets the estimated token cost of applying this action.
    /// </summary>
    public abstract int TokenCost { get; }

    /// <summary>
    /// Gets the calculated utility benefit of applying this action.
    /// Calculated using: U(item, option) = P_relevance × V(option, intent) × evidenceQuality × novelty
    /// </summary>
    public abstract double Benefit { get; }

    /// <summary>
    /// Gets the efficiency of this action (Benefit / TokenCost ratio).
    /// Used for greedy selection - higher efficiency actions are preferred.
    /// </summary>
    public double Efficiency => TokenCost > 0 ? Benefit / TokenCost : 0;

    /// <summary>
    /// Applies this action to the rendering state.
    /// </summary>
    /// <param name="state">The current rendering state to modify.</param>
    public abstract void Apply(RenderingState state);

    /// <summary>
    /// Gets a human-readable description of this action for debugging.
    /// </summary>
    public abstract string Description { get; }
}

/// <summary>
/// Action to include a new item at Minimal representation level (baseline inclusion).
/// </summary>
public sealed class IncludeItemAction : UpgradeAction
{
    private readonly SearchResult _result;
    private readonly Intent _intent;
    private readonly double _relevance;
    private readonly double _evidenceQuality;
    private readonly double _novelty;
    private readonly int _tokenCost;

    public IncludeItemAction(
        SearchResult result,
        Intent intent,
        double relevance,
        double evidenceQuality,
        double novelty,
        int tokenCost)
    {
        _result = result;
        _intent = intent;
        _relevance = relevance;
        _evidenceQuality = evidenceQuality;
        _novelty = novelty;
        _tokenCost = tokenCost;
    }

    public override int TokenCost => _tokenCost;

    public override double Benefit
    {
        get
        {
            // U(item, Minimal) = P_relevance × V(Minimal, intent) × evidenceQuality × novelty
            var optionValue = OptionValue.GetValue(_intent, RepresentationLevel.Minimal);
            return _relevance * optionValue * _evidenceQuality * _novelty;
        }
    }

    public override void Apply(RenderingState state)
    {
        state.Include(_result.Uri, RepresentationLevel.Minimal, TokenCost);
    }

    public override string Description =>
        $"Include {_result.Uri} at Minimal (confidence={_result.Confidence}, benefit={Benefit:F3}, efficiency={Efficiency:F3})";
}

/// <summary>
/// Action to upgrade an existing item from one representation level to another.
/// Valid upgrades: Minimal→Compact, Compact→Standard, Standard→Rich, or any larger jump.
/// </summary>
public sealed class ItemUpgradeAction : UpgradeAction
{
    private readonly SearchResult _result;
    private readonly Intent _intent;
    private readonly RepresentationLevel _fromLevel;
    private readonly RepresentationLevel _toLevel;
    private readonly double _relevance;
    private readonly double _evidenceQuality;
    private readonly double _novelty;

    public ItemUpgradeAction(
        SearchResult result,
        Intent intent,
        RepresentationLevel fromLevel,
        RepresentationLevel toLevel,
        double relevance,
        double evidenceQuality,
        double novelty)
    {
        if (!OptionValue.IsValidUpgrade(fromLevel, toLevel))
            throw new ArgumentException($"Invalid upgrade: {fromLevel} → {toLevel}");

        _result = result;
        _intent = intent;
        _fromLevel = fromLevel;
        _toLevel = toLevel;
        _relevance = relevance;
        _evidenceQuality = evidenceQuality;
        _novelty = novelty;
    }

    public override int TokenCost => OptionValue.GetUpgradeCost(_fromLevel, _toLevel);

    public override double Benefit
    {
        get
        {
            // Marginal benefit: benefit of target level minus benefit of current level
            var fromValue = OptionValue.GetValue(_intent, _fromLevel);
            var toValue = OptionValue.GetValue(_intent, _toLevel);
            var marginalValue = toValue - fromValue;

            // U(item, upgrade) = P_relevance × (V(to) - V(from)) × evidenceQuality × novelty
            return _relevance * marginalValue * _evidenceQuality * _novelty;
        }
    }

    public override void Apply(RenderingState state)
    {
        state.Upgrade(_result.Uri, _toLevel, TokenCost);
    }

    public override string Description =>
        $"Upgrade {_result.Uri} from {_fromLevel} to {_toLevel} (benefit={Benefit:F3}, efficiency={Efficiency:F3})";
}

/// <summary>
/// Action to reveal a child object under its parent document.
/// Child objects (e.g., methods within a file) are included at Minimal level.
/// </summary>
public sealed class ChildRevealAction : UpgradeAction
{
    private readonly string _parentUri;
    private readonly ObjectCandidate _child;
    private readonly Intent _intent;
    private readonly double _relevance;
    private readonly double _evidenceQuality;
    private readonly double _novelty;
    private readonly int _tokenCost;

    public ChildRevealAction(
        string parentUri,
        ObjectCandidate child,
        Intent intent,
        double relevance,
        double evidenceQuality,
        double novelty,
        int tokenCost)
    {
        _parentUri = parentUri;
        _child = child;
        _intent = intent;
        _relevance = relevance;
        _evidenceQuality = evidenceQuality;
        _novelty = novelty;
        _tokenCost = tokenCost;
    }

    public override int TokenCost => _tokenCost;

    public override double Benefit
    {
        get
        {
            // Child objects reveal specific symbols - use Minimal level value
            var optionValue = OptionValue.GetValue(_intent, RepresentationLevel.Minimal);
            return _relevance * optionValue * _evidenceQuality * _novelty;
        }
    }

    public override void Apply(RenderingState state)
    {
        state.RevealChild(_child.Uri, RepresentationLevel.Minimal, TokenCost);
    }

    public override string Description =>
        $"Reveal child {_child.Symbol ?? _child.Kind} under {_parentUri} (benefit={Benefit:F3}, efficiency={Efficiency:F3})";
}

/// <summary>
/// Generates all possible upgrade actions given the current rendering state.
/// </summary>
public static class UpgradeActionGenerator
{
    /// <summary>
    /// Generates all possible actions for the current rendering state.
    /// </summary>
    /// <param name="state">The current rendering state.</param>
    /// <param name="candidates">All search result candidates (both included and not yet included).</param>
    /// <param name="novelty">Novelty tracker for diminishing returns calculation.</param>
    /// <param name="intent">The user's intent (Explore, Find, Read).</param>
    /// <returns>A list of all possible upgrade actions, not yet sorted.</returns>
    public static IReadOnlyList<UpgradeAction> GenerateAllActions(
        RenderingState state,
        IReadOnlyList<SearchResult> candidates,
        NoveltyTracker novelty,
        Intent intent)
    {
        var actions = new List<UpgradeAction>();

        foreach (var candidate in candidates)
        {
            // Calculate common properties for this candidate
            var relevance = UtilityCalculator.CalculateRelevance(candidate.Confidence);

            // Determine evidence quality from the search result
            // For now, assume semantic score if confidence is high, lexical otherwise
            // This is a heuristic - could be improved with explicit flags on SearchResult
            var hasSemanticScore = candidate.Confidence >= 70;
            var hasLexicalHit = candidate.Confidence >= 40;
            var evidenceQuality = UtilityCalculator.CalculateEvidenceQuality(
                hasSemanticScore, hasLexicalHit, hasLexicalHit);

            // Get document URI for novelty tracking
            var documentUri = candidate.Scope == SearchScope.Document
                ? candidate.Uri
                : ExtractDocumentUri(candidate.Uri);

            var currentLevel = state.GetCurrentLevel(candidate.Uri);

            if (currentLevel == null)
            {
                // Item not yet included - generate include action
                var itemNovelty = novelty.GetCombinedNovelty(
                    candidate.Kind ?? "document",
                    documentUri);

                var tokenCost = EstimateTokenCost(candidate, RepresentationLevel.Minimal);

                actions.Add(new IncludeItemAction(
                    candidate,
                    intent,
                    relevance,
                    evidenceQuality,
                    itemNovelty,
                    tokenCost));
            }
            else
            {
                // Item already included - generate upgrade actions for all valid levels
                var itemNovelty = novelty.GetCombinedNovelty(
                    candidate.Kind ?? "document",
                    documentUri);

                foreach (var targetLevel in GetPossibleUpgrades(currentLevel.Value))
                {
                    actions.Add(new ItemUpgradeAction(
                        candidate,
                        intent,
                        currentLevel.Value,
                        targetLevel,
                        relevance,
                        evidenceQuality,
                        itemNovelty));
                }
            }

            // Generate child reveal actions if this is a document with child objects
            if (candidate.Scope == SearchScope.Document &&
                candidate.ChildObjects != null &&
                candidate.ChildObjects.Count > 0 &&
                state.IsIncluded(candidate.Uri)) // Only reveal children if parent is included
            {
                foreach (var child in candidate.ChildObjects)
                {
                    if (!state.IsChildRevealed(child.Uri))
                    {
                        var childNovelty = novelty.GetCombinedNovelty(
                            child.Kind ?? "object",
                            documentUri);

                        var childRelevance = UtilityCalculator.CalculateRelevance(child.Confidence);
                        var childHasSemanticScore = child.Confidence >= 70;
                        var childHasLexicalHit = child.Confidence >= 40;
                        var childEvidenceQuality = UtilityCalculator.CalculateEvidenceQuality(
                            childHasSemanticScore, childHasLexicalHit, childHasLexicalHit);

                        // Create ObjectCandidate from SearchResult (simplified for now)
                        // In real implementation, this would need proper ObjectCandidate data
                        var childCandidate = CreateObjectCandidateFromSearchResult(child);

                        var childTokenCost = EstimateChildTokenCost(child);

                        actions.Add(new ChildRevealAction(
                            candidate.Uri,
                            childCandidate,
                            intent,
                            childRelevance,
                            childEvidenceQuality,
                            childNovelty,
                            childTokenCost));
                    }
                }
            }
        }

        return actions;
    }

    /// <summary>
    /// Gets all valid upgrade levels from the current level.
    /// </summary>
    private static IEnumerable<RepresentationLevel> GetPossibleUpgrades(RepresentationLevel current)
    {
        // Return all levels higher than current
        var allLevels = OptionValue.GetLevelProgression().ToList();
        var currentIndex = allLevels.IndexOf(current);

        for (var i = currentIndex + 1; i < allLevels.Count; i++)
        {
            yield return allLevels[i];
        }
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

    private static int EstimateChildTokenCost(SearchResult child)
    {
        // Children shown at Minimal: kind badge + symbol + headline
        var baseCost = 15; // formatting overhead
        if (!string.IsNullOrEmpty(child.Symbol))
            baseCost += child.Symbol.Length / 4;
        if (!string.IsNullOrEmpty(child.Headline))
            baseCost += child.Headline.Length / 4;
        return baseCost;
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

    /// <summary>
    /// Creates an ObjectCandidate from a SearchResult.
    /// This is a simplified conversion - in practice, ObjectCandidate has more fields.
    /// </summary>
    private static ObjectCandidate CreateObjectCandidateFromSearchResult(SearchResult result)
    {
        return new ObjectCandidate
        {
            NodeId = result.Uri, // Simplified
            Uri = result.Uri,
            DocumentUri = ExtractDocumentUri(result.Uri),
            Kind = result.Kind ?? "unknown",
            Symbol = result.Symbol,
            Headline = result.Headline,
            Structure = result.Structure,
            Body = result.Snippet,
            LineStart = result.LineStart ?? 0,
            LineEnd = result.LineEnd ?? 0,
            StartByte = null,
            EndByte = null,
            Lang = result.Lang,
            SemanticType = result.SemanticType
        };
    }
}
