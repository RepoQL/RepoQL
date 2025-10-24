using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

internal sealed class SlnState
{
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }

    public string FormatVersion { get; init; } = string.Empty;
    public string VsVersion { get; init; } = string.Empty;
    public List<SlnProject> Projects { get; init; } = [];
    public List<SlnFolder> Folders { get; init; } = [];
    public List<string> Configurations { get; init; } = [];
    public Dictionary<string, string> NestedMappings { get; init; } = new();
}