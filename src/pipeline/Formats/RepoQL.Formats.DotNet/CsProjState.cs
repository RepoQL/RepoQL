using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

internal sealed class CsProjState
{
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }

    public string Sdk { get; init; } = string.Empty;
    public List<string> TargetFrameworks { get; init; } = [];
    public Dictionary<string, string> Properties { get; init; } = new();
    public List<CsPackage> Packages { get; init; } = [];
    public List<CsProjectRef> ProjectRefs { get; init; } = [];
    public string? OutputType { get; init; }
    public bool Pack { get; init; }
}