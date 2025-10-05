namespace RepoQL.Core.Metrics;

/// <summary>
/// Computes simple per-second rates from the InMemoryMetricsSink by comparing successive snapshots.
/// </summary>
public sealed class InMemoryRateProvider(InMemoryMetricsSink sink)
{
    private DateTime _last = DateTime.UtcNow;
    private IReadOnlyDictionary<string, double> _lastSnapshot = new Dictionary<string, double>();

    public RateSample Sample()
    {
        var now = DateTime.UtcNow;
        var cur = sink.Snapshot();
        var dt = Math.Max(0.001, (now - _last).TotalSeconds);

        double Rate(string name)
        {
            var prev = _lastSnapshot.TryGetValue(name, out var v) ? v : 0;
            var curr = cur.TryGetValue(name, out var c) ? c : 0;
            return Math.Max(0, (curr - prev) / dt);
        }

        var sample = new RateSample
        {
            FilesPerSecond = Rate("repoql.files.processed"),
            BytesPerSecond = Rate("repoql.bytes.processed"),
            NodesPerSecond = Rate("repoql.nodes.extracted"),
            DiscoverPerSecond = Rate("repoql.stage.discover"),
            HashPerSecond = Rate("repoql.stage.hash"),
            ParsePerSecond = Rate("repoql.stage.parse"),
            IndexPerSecond = Rate("repoql.stage.index"),
            FileSystemEventsPerSecond = Rate("repoql.fs.events")
        };

        _last = now;
        _lastSnapshot = cur;
        return sample;
    }
}

public sealed record RateSample
{
    public double FilesPerSecond { get; init; }
    public double BytesPerSecond { get; init; }
    public double NodesPerSecond { get; init; }
    public double DiscoverPerSecond { get; init; }
    public double HashPerSecond { get; init; }
    public double ParsePerSecond { get; init; }
    public double IndexPerSecond { get; init; }
    public double FileSystemEventsPerSecond { get; init; }
}