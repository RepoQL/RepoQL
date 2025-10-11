using System.Text.Json.Nodes;

namespace RepoQL.Contracts.Models;

public sealed class Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? SemanticKey { get; init; }
    public required string Kind { get; init; }
    public required string Severity { get; init; } // hint|info|warning|error (free text allowed)
    public required string Source { get; init; }
    public string? RuleId { get; init; }
    public required string Message { get; init; }
    public JsonObject Data { get; init; } = new();

    public required Guid ScopeDocumentId { get; init; }
    public Guid? TargetNodeId { get; init; }
    public Guid? TargetEdgeId { get; init; }
    public Guid? TargetSpanId { get; init; }
    public RepoUri? TargetUri { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; init; }
}