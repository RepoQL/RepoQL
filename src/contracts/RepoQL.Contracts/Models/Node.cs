using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RepoQL.Contracts.Models;

/// <summary>
///     A vertex in the property graph. Documents, sections, symbols, and other entities are nodes.
/// </summary>
[DebuggerDisplay("{Kind} {Uri}")]
public sealed record Node
{
    /// <summary>
    ///     Gets the node identifier. Value is a generated Guid.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    ///     Gets the open taxonomy string for this node, for example <c>document</c> or <c>md_section</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    ///     Gets the repository-aware URI for documents. For non-document nodes the value is <c>null</c>.
    ///     The fragment is not stored in the database and should be omitted.
    /// </summary>
    public RepoUri? Uri { get; init; }

    /// <summary>
    ///     Gets the artifact identifier for document nodes that point to concrete bytes.
    /// </summary>
    public Guid? ArtifactId { get; init; }

    /// <summary>
    ///     Gets the span identifier when this node corresponds to a text range within a document.
    /// </summary>
    public Guid? SpanId { get; init; }

    /// <summary>
    ///     Gets the property bag for arbitrary attributes. Keys should be short and stable.
    /// </summary>
    public JsonObject Props { get; init; } = new();

    /// <summary>
    ///     Optional headline (X-ray Level 0) describing this node.
    /// </summary>
    public string? Headline { get; init; }

    /// <summary>
    ///     Optional outline/structure (X-ray Level 2) for this node.
    /// </summary>
    public string? Structure { get; init; }

    /// <summary>
    ///     Gets the creation timestamp in UTC. Set by the ingester.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Gets the update timestamp in UTC. Update when the node changes materially.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
