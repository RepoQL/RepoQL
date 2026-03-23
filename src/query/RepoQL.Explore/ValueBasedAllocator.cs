namespace RepoQL.Explore;

/// <summary>
/// Allocates token budget using a two-level hierarchical algorithm.
/// Level 1: Files compete for budget based on file-level EV (considers best child).
/// Level 2: Within each file's budget, file and children compete for representation.
///
/// Breadth (1-10) controls allocation curve via a sigmoid applied to
/// confidence scores before proportional allocation:
/// - Low breadth (1-3): steep sigmoid → budget concentrated on top results, noise starved
/// - Medium breadth (5): moderate sigmoid → balanced allocation
/// - High breadth (7-10): gentle sigmoid → budget spread more evenly across results
/// </summary>
public static class ValueBasedAllocator
{
    /// <summary>
    /// Allocate representations using hierarchical budget allocation.
    /// </summary>
    /// <param name="results">Top-level files with ChildObjects.</param>
    /// <param name="tokenBudget">Total token budget to allocate.</param>
    /// <param name="breadth">Breadth 1-10 controlling allocation curve steepness.</param>
    /// <returns>Rendering decisions with allocated representation levels.</returns>
    public static List<RenderingDecision> Allocate(
        IReadOnlyList<ExploreResult> results,
        int tokenBudget,
        int breadth)
    {
        if (results.Count == 0)
            return [];

        var clampedBreadth = Math.Clamp(breadth, 1, 10);

        // Level 1: Calculate file-level EV and allocate budget to files
        var k = GetSigmoidK(clampedBreadth);
        var files = results.Select(r => new FileAllocation
        {
            Result = r,
            ExpectedValue = CalculateFileEV(r, k),
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
        var decisions = files.Select(f => AllocateWithinFile(f.Result, f.Budget, clampedBreadth, k)).ToList();

        // Level 3: Global upgrade pass — per-file allocation often leaves budget in a dead zone
        // (enough for Compact but not Standard). Collect unused tokens and upgrade top files.
        var totalUsed = decisions.Sum(TotalDecisionTokens);
        var globalRemaining = tokenBudget - totalUsed;

        if (globalRemaining > 0)
        {
            for (var i = 0; i < decisions.Count && globalRemaining > 0; i++)
            {
                var d = decisions[i];
                var nextLevel = GetNextLevel(d.Level);
                if (nextLevel is null) continue;

                var nextCost = ExploreTokenEstimator.Estimate(d.Result, nextLevel.Value);
                var upgradeCost = nextCost - d.EstimatedTokens;

                if (upgradeCost > 0 && upgradeCost <= globalRemaining)
                {
                    decisions[i] = d with { Level = nextLevel.Value, EstimatedTokens = nextCost };
                    globalRemaining -= upgradeCost;
                }
            }
        }

        return decisions;
    }

    private static int TotalDecisionTokens(RenderingDecision d)
        => d.EstimatedTokens + (d.ChildDecisions?.Sum(c => c.EstimatedTokens) ?? 0);

    /// <summary>
    /// Sigmoid midpoint for budget allocation. Scores below this get
    /// diminished budget; above get amplified. Set to 0.35 (35% confidence)
    /// so results below ~35% confidence are starved at low breadth.
    /// </summary>
    private const double SigmoidMidpoint = 0.35;

    /// <summary>
    /// Sigmoid steepness derived from breadth.
    /// Low breadth (depth) → steep sigmoid → concentrate budget on top results.
    /// High breadth (coverage) → gentle sigmoid → spread budget evenly.
    /// </summary>
    internal static double GetSigmoidK(int breadth)
    {
        var clamped = Math.Clamp(breadth, 1, 10);
        // breadth 1 → k=14 (steep), 5 → k=7 (moderate), 10 → k=2 (gentle)
        return 14.0 - (clamped - 1) * (12.0 / 9.0);
    }

    /// <summary>
    /// Apply sigmoid to a 0-100 confidence score for budget allocation.
    /// Output is a relative expected value (not a percentage).
    /// </summary>
    private static double SigmoidEV(double confidence, double k)
    {
        var x = Math.Max(confidence, 0) / 100.0;
        return 1.0 / (1.0 + Math.Exp(-k * (x - SigmoidMidpoint)));
    }

    /// <summary>
    /// Maximum children to show per file, derived from breadth.
    /// Low breadth = more children (depth), high breadth = fewer children (breadth).
    /// </summary>
    private static int GetMaxChildren(int breadth)
        => breadth switch
        {
            <= 2 => 8,
            <= 4 => 6,
            <= 6 => 5,
            <= 8 => 3,
            _ => 2
        };

    /// <summary>
    /// Calculate file-level expected value via sigmoid.
    /// Uses max of file confidence and best child confidence.
    /// </summary>
    private static double CalculateFileEV(ExploreResult file, double k)
    {
        var bestChildConf = file.ChildObjects?.Count > 0
            ? file.ChildObjects.Max(c => c.Confidence)
            : 0;
        var baseConf = Math.Max(file.Confidence, bestChildConf);
        return SigmoidEV(baseConf, k);
    }

    /// <summary>
    /// Allocate budget within a single file among the file and its children.
    /// </summary>
    private static RenderingDecision AllocateWithinFile(
        ExploreResult file,
        int fileBudget,
        int breadth,
        double k)
    {
        // Build candidate list: file (without children) + all children
        var items = new List<AllocationItem>
        {
            new()
            {
                Result = file with { ChildObjects = null },
                ExpectedValue = SigmoidEV(file.Confidence, k),
                Level = Representation.Minimal,
                Tokens = ExploreTokenEstimator.EstimateMinimal(file)
            }
        };

        var omittedChildrenCount = 0;

        if (file.ChildObjects != null && file.ChildObjects.Count > 0)
        {
            var maxChildren = GetMaxChildren(breadth);

            // Score and sort children by EV, take top N
            var scoredChildren = file.ChildObjects
                .Select(c => new AllocationItem
                {
                    Result = c,
                    ExpectedValue = SigmoidEV(c.Confidence, k),
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
                item.Level = PickBestFit(item.Result, allocation);
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
    /// Falls through to Minimal (URI-only) when budget can't afford Compact — a visible
    /// URI is better than an invisible result. The sigmoid concentrates budget on what
    /// matters; the tail gets awareness, not depth.
    /// </summary>
    private static Representation PickBestFit(ExploreResult result, int allocation)
    {
        if (ExploreTokenEstimator.EstimateRich(result) <= allocation)
            return Representation.Rich;
        if (ExploreTokenEstimator.EstimateStandard(result) <= allocation)
            return Representation.Standard;
        if (ExploreTokenEstimator.EstimateCompact(result) <= allocation)
            return Representation.Compact;

        return Representation.Minimal;
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
