using System.Text.Json.Nodes;

namespace RepoQL.Contracts.Models;

/// <summary>
///     A lint finding, metric, or other fact attached to a document or element.
/// </summary>
public sealed record Annotation
{
    /// <summary>
    ///     Gets the annotation identifier. Value is a generated Guid.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    ///     Gets an optional semantic key for upsert deduplication.
    /// </summary>
    public string? SemanticKey { get; init; }

    /// <summary>
    ///     Gets the kind of annotation (e.g., "lint", "metric", "fact").
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    ///     Gets the severity level (hint|info|warning|error, free text allowed).
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    ///     Gets the source analyzer or tool that produced this annotation.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    ///     Gets the optional rule identifier.
    /// </summary>
    public string? RuleId { get; init; }

    /// <summary>
    ///     Gets the human-readable message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    ///     Gets additional structured data for this annotation.
    /// </summary>
    public JsonObject Data { get; init; } = new();

    /// <summary>
    ///     Gets the identifier of the document this annotation is scoped to.
    /// </summary>
    public required Guid ScopeDocumentId { get; init; }

    /// <summary>
    ///     Gets the optional target node identifier.
    /// </summary>
    public Guid? TargetNodeId { get; init; }

    /// <summary>
    ///     Gets the optional target edge identifier.
    /// </summary>
    public Guid? TargetEdgeId { get; init; }

    /// <summary>
    ///     Gets the optional target span identifier.
    /// </summary>
    public Guid? TargetSpanId { get; init; }

    /// <summary>
    ///     Gets the optional target URI.
    /// </summary>
    public RepoUri? TargetUri { get; init; }

    /// <summary>
    ///     Gets the creation timestamp in UTC.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Gets the optional expiration timestamp.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
