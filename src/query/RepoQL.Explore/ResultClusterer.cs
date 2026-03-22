namespace RepoQL.Explore;

/// <summary>
/// Groups render decisions into path-based clusters for display.
/// </summary>
public static class ResultClusterer
{
    public const int ClusterHeaderTokenCost = 15;

    private const int MinResultsForClustering = 6;
    private const int MinMembersForCluster = 2;
    private const int MinMembersForHeader = 3;

    /// <summary>
    /// Cluster decisions by parent directory using URI path.
    /// </summary>
    public static ClusteredOutput Cluster(List<RenderingDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        if (decisions.Count < MinResultsForClustering)
            return new ClusteredOutput([.. decisions]);

        var grouped = new Dictionary<string, ClusterCandidate>(StringComparer.OrdinalIgnoreCase);
        var ungrouped = new List<IndexedDecision>();

        foreach (var (decision, index) in decisions.Select((d, i) => (d, i)))
        {
            if (!TryGetParentDirectory(decision.Result.Uri, out var key, out var sharedPath))
            {
                ungrouped.Add(new IndexedDecision(decision, index));
                continue;
            }

            if (!grouped.TryGetValue(key, out var candidate))
            {
                candidate = new ClusterCandidate(sharedPath);
                grouped[key] = candidate;
            }

            candidate.Members.Add(new IndexedDecision(decision, index));
        }

        var orderedUnits = new List<OrderedUnit>();

        foreach (var candidate in grouped.Values)
        {
            if (candidate.Members.Count < MinMembersForCluster)
            {
                ungrouped.AddRange(candidate.Members);
                continue;
            }

            candidate.Members.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            orderedUnits.Add(OrderedUnit.FromCluster(candidate));
        }

        orderedUnits.AddRange(ungrouped.Select(OrderedUnit.FromSingle));
        orderedUnits.Sort(static (a, b) =>
        {
            var confidenceComparison = b.MaxConfidence.CompareTo(a.MaxConfidence);
            return confidenceComparison != 0
                ? confidenceComparison
                : a.FirstIndex.CompareTo(b.FirstIndex);
        });

        var outputItems = new List<OutputItem>();
        foreach (var unit in orderedUnits)
        {
            if (unit.Cluster is { } cluster)
            {
                if (cluster.Members.Count >= MinMembersForHeader)
                    outputItems.Add(new ClusterHeader(cluster.SharedPath, cluster.Members.Count));

                outputItems.AddRange(cluster.Members.Select(m => (OutputItem)m.Decision));
                continue;
            }

            outputItems.Add(unit.Single!.Decision);
        }

        return new ClusteredOutput(outputItems);
    }

    private static bool TryGetParentDirectory(string uri, out string key, out string sharedPath)
    {
        key = string.Empty;
        sharedPath = string.Empty;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return false;

        var fullPath = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var pathSplitIndex = fullPath.LastIndexOf('/');
        if (pathSplitIndex < 0)
            return false;

        var parentPathWithScheme = fullPath[..pathSplitIndex];
        if (string.IsNullOrWhiteSpace(parentPathWithScheme))
            return false;

        var absolutePath = parsed.AbsolutePath.TrimEnd('/');
        var absolutePathSplitIndex = absolutePath.LastIndexOf('/');
        if (absolutePathSplitIndex < 0)
            return false;

        var directoryPath = absolutePath[..absolutePathSplitIndex].Trim('/');
        var includeAuthority = !string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(parsed.Scheme, "help", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(parsed.Authority);

        var displayPrefix = includeAuthority ? $"{parsed.Authority.TrimEnd('/')}/" : string.Empty;
        var displayPath = string.IsNullOrWhiteSpace(directoryPath)
            ? displayPrefix.TrimEnd('/')
            : $"{displayPrefix}{Uri.UnescapeDataString(directoryPath)}";

        if (string.IsNullOrWhiteSpace(displayPath))
            displayPath = "/";

        if (!displayPath.EndsWith("/", StringComparison.Ordinal))
            displayPath += "/";

        key = parentPathWithScheme;
        sharedPath = displayPath;
        return true;
    }

    private sealed class ClusterCandidate(string sharedPath)
    {
        public string SharedPath { get; } = sharedPath;

        public List<IndexedDecision> Members { get; } = [];
    }

    private sealed record IndexedDecision(RenderingDecision Decision, int Index);

    private sealed record OrderedUnit(
        ClusterCandidate? Cluster,
        IndexedDecision? Single,
        int MaxConfidence,
        int FirstIndex)
    {
        public static OrderedUnit FromCluster(ClusterCandidate cluster)
            => new(
                Cluster: cluster,
                Single: null,
                MaxConfidence: cluster.Members.Max(m => m.Decision.Result.Confidence),
                FirstIndex: cluster.Members.Min(m => m.Index));

        public static OrderedUnit FromSingle(IndexedDecision single)
            => new(
                Cluster: null,
                Single: single,
                MaxConfidence: single.Decision.Result.Confidence,
                FirstIndex: single.Index);
    }
}

/// <summary>
/// Base output item for rendered explore output.
/// </summary>
public abstract record OutputItem;

/// <summary>
/// Cluster header row shown above grouped members.
/// </summary>
/// <param name="SharedPath">Shared parent path (e.g. src/Auth/).</param>
/// <param name="MemberCount">Number of grouped members.</param>
public sealed record ClusterHeader(string SharedPath, int MemberCount) : OutputItem;

/// <summary>
/// Clustered result output payload.
/// </summary>
/// <param name="Items">Ordered list of headers and rendering decisions.</param>
public sealed record ClusteredOutput(IReadOnlyList<OutputItem> Items)
{
    public int HeaderCount => Items.Count(static i => i is ClusterHeader);
}
