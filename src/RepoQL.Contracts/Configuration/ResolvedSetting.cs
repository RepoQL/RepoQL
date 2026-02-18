namespace RepoQL.Contracts.Configuration;

/// <summary>
/// A setting's resolved value together with its provenance — which scope the winning value came from.
/// </summary>
public sealed record ResolvedSetting(
    string Key,
    object? Value,
    ConfigScope Source);
