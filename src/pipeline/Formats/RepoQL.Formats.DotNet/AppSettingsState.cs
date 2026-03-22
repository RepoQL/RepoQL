using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

internal sealed class AppSettingsState
{
    // Core metadata
    public required string Digest { get; init; }
    public required long Size { get; init; }
    public required SemanticMediaType MediaType { get; init; }
    public required string StoreUri { get; init; }

    // Context (what's actually in the file)
    public string? Environment { get; init; }  // "Development", "Production" from filename
    public List<string> TopLevelKeys { get; init; } = [];
    public List<string> ConnectionStringNames { get; init; } = [];
    public List<string> DetectedServices { get; init; } = [];

    // Warnings
    public List<PotentialSecret> PotentialSecrets { get; init; } = [];
}

internal sealed record PotentialSecret(string Path, int Line);
