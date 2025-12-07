using System.Diagnostics;
using System.Text.Json.Nodes;
using RepoQL.Contracts.Data;

namespace RepoQL.Contracts.Models;

/// <summary>
///     A directed relationship between two nodes with optional attributes and source or destination spans.
/// </summary>
[DebuggerDisplay("{Type} {SrcId} -> {DstId}")]
public sealed class Edge
{
    /// <summary>
    ///     Gets the edge identifier. Value is a generated Guid.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    ///     Gets the source node identifier.
    /// </summary>
    public required Guid SrcId { get; init; }

    /// <summary>
    ///     Gets the destination node identifier. Nullable for unresolved references.
    /// </summary>
    public Guid? DstId { get; init; }

    /// <summary>
    ///     Gets the destination URI for reference edges. Enables deferred resolution and broken-link detection.
    /// </summary>
    public RepoUri? DstUri { get; init; }

    /// <summary>
    ///     Gets the relationship type token, for example <c>HAS_PART</c> or <c>REFERS_TO</c>.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    ///     Gets a value indicating whether this edge expresses composition or containment.
    /// </summary>
    public bool IsComposition { get; init; }

    /// <summary>
    ///     Gets the optional stable order among composition siblings.
    /// </summary>
    public int? Ordinal { get; init; }

    /// <summary>
    ///     Gets the identifier of the document that owns or scoped the analysis that produced this edge.
    /// </summary>
    public Guid? ScopeDocumentId { get; init; }

    /// <summary>
    ///     Gets an optional key that enforces semantic uniqueness for this relation.
    ///     When set, <see cref="IGraphStore.UpsertEdge" /> updates by key.
    /// </summary>
    public string? EdgeKey { get; init; }

    /// <summary>
    ///     Gets the span identifier of the origin site in the source document, for example a call site or link text.
    /// </summary>
    public Guid? SrcSpanId { get; init; }

    /// <summary>
    ///     Gets the span identifier that the relation points to in the destination document.
    /// </summary>
    public Guid? DstSpanId { get; init; }

    /// <summary>
    ///     Gets the attribute bag for relation metadata, for example <c>href</c>, <c>label</c> or <c>role</c>.
    /// </summary>
    public JsonObject Props { get; init; } = new();

    /// <summary>
    ///     Gets the creation timestamp in UTC for this edge.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Validates basic invariants for this edge and throws if they are violated.
    /// </summary>
    public void Validate()
    {
        if (Id == Guid.Empty) throw new InvalidOperationException("Edge Id is required.");
        if (SrcId == Guid.Empty) throw new InvalidOperationException("Source node is required.");
        if (string.IsNullOrWhiteSpace(Type)) throw new InvalidOperationException("Edge type is required.");

        // Composition edges require a resolved destination
        if (IsComposition && DstId is null)
            throw new InvalidOperationException("Composition edge requires destination node id.");

        // Reference edges need either DstId or DstUri
        if (!IsComposition && DstId is null && DstUri is null)
            throw new InvalidOperationException("Reference edge requires destination node id or URI.");

        // Self-edges are allowed unless this is a composition relation.
        if (IsComposition && SrcId == DstId)
            throw new InvalidOperationException("Composition edge cannot point to itself.");
    }
}