namespace RepoQL.Explore;

/// <summary>
/// Allocates token budget using a two-level hierarchical algorithm.
/// Level 1: Files compete for budget based on file-level EV (considers best child).
/// Level 2: Within each file's budget, file and children compete for representation.
/// </summary>
public static class ValueBasedAllocator
{
    /// <summary>
    /// Allocate representations using hierarchical budget allocation.
    /// </summary>
    /// <param name="results">Top-level files with ChildObjects.</param>
    /// <param name="tokenBudget">Total token budget to allocate.</param>
    /// <param name="intent">Search intent (affects allocation curve).</param>
    /// <returns>Rendering decisions with allocated representation levels.</returns>
    public static List<RenderingDecision> Allocate(
        IReadOnlyList<ExploreResult> results,
        int tokenBudget,
        Intent intent)
    {
        if (results.Count == 0)
            return [];

        // Level 1: Calculate file-level EV and allocate budget to files
        var files = results.Select(r => new FileAllocation
        {
            Result = r,
            ExpectedValue = CalculateFileEV(r, intent),
            Budget = 0,
            MinCost = ExploreTokenEstimator.EstimateMinimal(r)
        }).ToList();

        // Proportional budget allocation to files
        var totalEV = files.Sum(f => f.ExpectedValue);
        if (totalEV > 0)
        {
            foreach (var file in files)
                file.Budget = (int)(tokenBudget * file.ExpectedValue / totalEV);
        }

        // Level 1.5: Drop lowest-EV files if minimum costs exceed budget
        var totalMinCost = files.Sum(f => f.MinCost);
        if (totalMinCost > tokenBudget)
        {
            files = files.OrderBy(f => f.ExpectedValue).ToList();
            while (files.Count > 0 && totalMinCost > tokenBudget)
            {
                totalMinCost -= files[0].MinCost;
                files.RemoveAt(0);
            }

            // Reallocate budget among surviving files
            totalEV = files.Sum(f => f.ExpectedValue);
            if (totalEV > 0)
            {
                foreach (var file in files)
                    file.Budget = (int)(tokenBudget * file.ExpectedValue / totalEV);
            }
        }

        // Level 2: Allocate within each file
        return files.Select(f => AllocateWithinFile(f.Result, f.Budget, intent)).ToList();
    }

    /// <summary>
    /// Calculate file-level expected value.
    /// Uses max of file confidence and best child confidence.
    /// </summary>
    private static double CalculateFileEV(ExploreResult file, Intent intent)
    {
        var bestChildConf = file.ChildObjects?.Count > 0
            ? file.ChildObjects.Max(c => c.Confidence)
            : 0;
        var baseEV = Math.Max(file.Confidence, bestChildConf);
        return baseEV * GetIntentModifier(intent);
    }

    /// <summary>
    /// Get intent modifier for EV calculation.
    /// </summary>
    private static double GetIntentModifier(Intent intent) => intent switch
    {
        Intent.Inspect => 1.2,       // Concentrate on top results
        Intent.Locate => 1.0,          // Balanced
        Intent.Inventory => 0.8,       // Flatten distribution
        Intent.Explain => 1.1,    // Balanced but focused - LLM needs context not noise
        _ => 1.0
    };

    /// <summary>
    /// Maximum children to show per file by intent.
    /// Explore: fewer children (breadth over depth)
    /// Find: moderate children
    /// Examine: more children (depth over breadth)
    /// Understand: moderate depth - LLM needs context not noise
    /// </summary>
    private static int GetMaxChildrenForIntent(Intent intent) => intent switch
    {
        Intent.Inventory => 3,
        Intent.Locate => 5,
        Intent.Inspect => 8,
        Intent.Explain => 6,
        _ => 5
    };

    /// <summary>
    /// Allocate budget within a single file among the file and its children.
    /// </summary>
    private static RenderingDecision AllocateWithinFile(
        ExploreResult file,
        int fileBudget,
        Intent intent)
    {
        // Build candidate list: file (without children) + all children
        var items = new List<AllocationItem>
        {
            new()
            {
                Result = file with { ChildObjects = null },
                ExpectedValue = file.Confidence * GetIntentModifier(intent),
                Level = Representation.Minimal,
                Tokens = ExploreTokenEstimator.EstimateMinimal(file)
            }
        };

        var omittedChildrenCount = 0;

        if (file.ChildObjects != null && file.ChildObjects.Count > 0)
        {
            var maxChildren = GetMaxChildrenForIntent(intent);

            // Score and sort children by EV, take top N
            var scoredChildren = file.ChildObjects
                .Select(c => new AllocationItem
                {
                    Result = c,
                    ExpectedValue = c.Confidence * GetIntentModifier(intent),
                    Level = Representation.Minimal,
                    Tokens = ExploreTokenEstimator.EstimateMinimal(c)
                })
                .OrderByDescending(c => c.ExpectedValue)
                .ToList();

            // Take top N children, track omitted
            var selectedChildren = scoredChildren.Take(maxChildren).ToList();
            omittedChildrenCount = scoredChildren.Count - selectedChildren.Count;

            items.AddRange(selectedChildren);
        }

        // Pass 1: Proportional allocation within file budget
        var totalEV = items.Sum(i => i.ExpectedValue);
        if (totalEV > 0)
        {
            foreach (var item in items)
            {
                var allocation = (int)(fileBudget * item.ExpectedValue / totalEV);
                item.Level = PickBestFit(item.Result, allocation, intent);
                item.Tokens = ExploreTokenEstimator.Estimate(item.Result, item.Level);
            }
        }

        // Pass 1.5: Drop lowest-EV children if over budget (never drop the file itself)
        var totalTokens = items.Sum(i => i.Tokens);
        if (totalTokens > fileBudget && items.Count > 1)
        {
            var children = items.Skip(1).OrderBy(i => i.ExpectedValue).ToList();
            while (children.Count > 0 && totalTokens > fileBudget)
            {
                var drop = children[0];
                totalTokens -= drop.Tokens;
                children.RemoveAt(0);
            }
            items = new List<AllocationItem> { items[0] }.Concat(children).ToList();
        }

        // Pass 2: Upgrade stragglers with remaining budget
        var remaining = fileBudget - items.Sum(i => i.Tokens);
        var sorted = items.OrderByDescending(i => i.ExpectedValue).ToList();
        var upgraded = true;
        while (upgraded && remaining > 0)
        {
            upgraded = false;
            foreach (var item in sorted)
            {
                if (remaining <= 0) break;

                var nextLevel = GetNextLevel(item.Level);
                if (nextLevel is null) continue;

                var nextCost = ExploreTokenEstimator.Estimate(item.Result, nextLevel.Value);
                var upgradeCost = nextCost - item.Tokens;

                if (upgradeCost <= remaining)
                {
                    item.Level = nextLevel.Value;
                    item.Tokens = nextCost;
                    remaining -= upgradeCost;
                    upgraded = true;
                }
            }
        }

        // Build nested decision structure
        var fileItem = items[0];
        var childDecisions = items.Count > 1
            ? items.Skip(1).Select(c => new RenderingDecision(c.Result, c.Level, c.Tokens)).ToList()
            : null;

        return new RenderingDecision(fileItem.Result, fileItem.Level, fileItem.Tokens, childDecisions, omittedChildrenCount);
    }

    /// <summary>
    /// Pick the richest representation that fits within the token allocation.
    /// Minimal (no URI) is only used for Explore; Find/Examine/Understand use Compact as floor.
    /// </summary>
    private static Representation PickBestFit(ExploreResult result, int allocation, Intent intent)
    {
        if (ExploreTokenEstimator.EstimateRich(result) <= allocation)
            return Representation.Rich;
        if (ExploreTokenEstimator.EstimateStandard(result) <= allocation)
            return Representation.Standard;
        if (ExploreTokenEstimator.EstimateCompact(result) <= allocation)
            return Representation.Compact;

        // Minimal (no URI) only for Explore - URI is high-value for Find/Examine/Understand
        return intent == Intent.Inventory ? Representation.Minimal : Representation.Compact;
    }

    /// <summary>
    /// Get the next richer representation level.
    /// </summary>
    private static Representation? GetNextLevel(Representation current)
        => current switch
        {
            Representation.Minimal => Representation.Compact,
            Representation.Compact => Representation.Standard,
            Representation.Standard => Representation.Rich,
            Representation.Rich => null,
            _ => null
        };

    private class FileAllocation
    {
        public required ExploreResult Result { get; init; }
        public double ExpectedValue { get; set; }
        public int Budget { get; set; }
        public int MinCost { get; set; }
    }

    private class AllocationItem
    {
        public required ExploreResult Result { get; init; }
        public double ExpectedValue { get; set; }
        public Representation Level { get; set; }
        public int Tokens { get; set; }
    }
}
