namespace RepoQL.Sarif.Models;

/// <summary>
/// Aggregate outcome of importing one SARIF file.
/// </summary>
public sealed record SarifImportResult(
    IReadOnlyList<SourceImportResult> Sources,
    int TotalFindings,
    int ResolvedToFiles,
    int UnresolvedPaths,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Per-source import counters.
/// </summary>
public sealed record SourceImportResult(
    string Source,
    int Total,
    int New,
    int Updated,
    int Unchanged,
    int Expired,
    int Resolved,
    int Unresolved);
