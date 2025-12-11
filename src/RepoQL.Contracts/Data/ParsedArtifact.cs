using RepoQL.Contracts.Models;

namespace RepoQL.Contracts.Data;

/// <summary>
/// A parsed artifact ready for indexing: the artifact metadata plus its
/// document node, child nodes, spans, and edges.
/// </summary>
public sealed class ParsedArtifact
{
    /// <summary>
    /// The artifact (content metadata, digest, size).
    /// </summary>
    public required Artifact Artifact { get; init; }

    /// <summary>
    /// The root document node (kind='document').
    /// </summary>
    public required Node DocumentNode { get; init; }

    /// <summary>
    /// Child nodes within the document.
    /// </summary>
    public IReadOnlyList<Node> Children { get; init; } = [];

    /// <summary>
    /// Spans (locations) within the document.
    /// </summary>
    public IReadOnlyList<Span> Spans { get; init; } = [];

    /// <summary>
    /// Edges (relationships) scoped to this document.
    /// </summary>
    public IReadOnlyList<Edge> Edges { get; init; } = [];

    /// <summary>
    /// Annotations (lint warnings, metrics, etc.) for this document.
    /// </summary>
    public IReadOnlyList<Annotation> Annotations { get; init; } = [];

    /// <summary>
    /// Annotation source identifiers (for tracking which analyzers produced annotations).
    /// </summary>
    public IReadOnlyList<string> AnnotationSources { get; init; } = [];

    /// <summary>
    /// Create a <see cref="ParsedArtifact"/> from parser <see cref="Records"/> output.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if records has no artifact or no document node.
    /// </exception>
    public static ParsedArtifact FromRecords(Records records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var artifact = records.Artifacts.FirstOrDefault()
            ?? throw new ArgumentException("Records must have at least one artifact", nameof(records));

        var docNode = records.Nodes.FirstOrDefault(n =>
            string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Records must have a document node", nameof(records));

        var children = records.Nodes
            .Where(n => !string.Equals(n.Kind, "document", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new ParsedArtifact
        {
            Artifact = artifact,
            DocumentNode = docNode,
            Children = children,
            Spans = records.Spans,
            Edges = records.Edges,
            Annotations = records.Annotations,
            AnnotationSources = records.AnnotationSources
        };
    }
}
