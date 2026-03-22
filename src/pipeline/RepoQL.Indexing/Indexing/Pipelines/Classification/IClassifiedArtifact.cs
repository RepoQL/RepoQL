using RepoQL.Contracts;
using RepoQL.Indexing.Indexing.Pipelines.Discovery;

namespace RepoQL.Indexing.Indexing.Pipelines.Classification;

public interface IClassifiedArtifact : IDiscoveredArtifact
{
    /// <summary>
    ///     Resolved semantic media type (populated by classification stage).
    ///     Processors cannot modify this - they return new results via ProcessAsync.
    /// </summary>
    public SemanticMediaType? MediaType { get; }
}