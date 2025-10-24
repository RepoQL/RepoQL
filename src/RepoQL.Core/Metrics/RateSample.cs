namespace RepoQL.Core.Metrics;

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