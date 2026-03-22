namespace RepoQL.Formats.Json;

public sealed record JsonParseOptions
{
    public int MaxSampleRecords { get; init; } = 100;
    public int MaxNodeDepth { get; init; } = 2;
    public int MaxNodes { get; init; } = 200;
    public bool IsJsonl { get; init; }
}
