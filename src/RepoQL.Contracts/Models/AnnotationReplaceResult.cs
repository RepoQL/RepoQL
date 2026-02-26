namespace RepoQL.Contracts.Models;

/// <summary>
/// Result counters for a source-wide annotation replacement operation.
/// </summary>
/// <param name="Inserted">Number of newly inserted annotations.</param>
/// <param name="Updated">Number of existing annotations that were updated.</param>
/// <param name="Expired">Number of stale annotations deleted during replacement.</param>
public sealed record AnnotationReplaceResult(int Inserted, int Updated, int Expired);
