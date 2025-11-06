using RepoQL.Contracts.Models;
using RepoQL.Indexing.Indexing.Pipelines.Parsing;

namespace RepoQL.Indexing.Indexing.Pipelines.Analysis;

public interface IAnnotatedArtifact : IParsedArtifact
{
    /// <summary>
    ///     Annotations accumulated from analyzers (read-only).
    ///     Processors cannot modify this collection - they return new annotations via ProcessAsync.
    /// </summary>
    public IReadOnlyList<Annotation> Annotations { get; }
}