using System.Text.Json.Nodes;

namespace RepoQL.Sarif.Models;

/// <summary>
/// Normalization output for one SARIF file.
/// </summary>
public sealed record NormalizationResult(
    IReadOnlyList<NormalizedRun> Runs,
    int SkippedResults,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Normalized result set for one SARIF run/source.
/// </summary>
public sealed record NormalizedRun(
    string Source,
    IReadOnlyList<NormalizedResult> Results);

/// <summary>
/// A single normalized finding from SARIF.
/// </summary>
public sealed record NormalizedResult(
    string RuleId,
    string Message,
    string Level,
    string NormalizedPath,
    NormalizedRegion? Region,
    IReadOnlyDictionary<string, string>? PartialFingerprints,
    IReadOnlyDictionary<string, string>? Fingerprints,
    JsonObject? RuleMetadata,
    JsonObject? Data);

/// <summary>
/// Line/column region normalized from SARIF coordinates.
/// </summary>
public sealed record NormalizedRegion(
    int StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn);
