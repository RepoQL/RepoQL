using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines.Classification;

namespace RepoQL.Indexing.Indexing.Pipelines.Parsing;

public interface IParsedArtifact : IClassifiedArtifact
{
    /// <summary>
    ///     Materialized graph records (artifacts, nodes, spans, edges).
    ///     Processors cannot modify this - they return new results via ProcessAsync.
    /// </summary>
    public Records? Records { get; }
}