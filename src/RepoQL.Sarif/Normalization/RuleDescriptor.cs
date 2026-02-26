using System.Text.Json.Nodes;

namespace RepoQL.Sarif.Normalization;

/// <summary>
/// Normalized rule metadata collected from a SARIF run.
/// </summary>
public sealed record RuleDescriptor(
    string Id,
    string? DefaultLevel,
    IReadOnlyDictionary<string, string> MessageStrings,
    JsonObject? Metadata,
    JsonObject? Properties);
