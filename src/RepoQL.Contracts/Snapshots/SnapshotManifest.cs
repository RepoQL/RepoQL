namespace RepoQL.Contracts.Snapshots;

/// <summary>
/// Serialization envelope for a snapshot: metadata plus the document payloads.
/// This is what gets written to / read from JSON.
/// </summary>
public sealed class SnapshotManifest
{
    /// <summary>
    /// Manifest format version. Bump when the serialization shape changes.
    /// </summary>
    public required string FormatVersion { get; init; }

    /// <summary>
    /// The snapshot source identifier (matches <see cref="ISnapshotSource.Id"/>).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// The snapshot version (matches <see cref="ISnapshotSource.Version"/>).
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// The serialized documents.
    /// </summary>
    public required IReadOnlyList<SnapshotDocumentDto> Documents { get; init; }
}

/// <summary>
/// Serializable DTO for a single document's graph data.
/// Mirrors <see cref="SnapshotDocument"/> but uses DTOs suitable for JSON round-tripping.
/// </summary>
public sealed class SnapshotDocumentDto
{
    /// <summary>
    /// The document URI as a string.
    /// </summary>
    public required string Uri { get; init; }

    /// <summary>
    /// The artifact record.
    /// </summary>
    public required ArtifactDto Artifact { get; init; }

    /// <summary>
    /// All nodes (document node + children).
    /// </summary>
    public required IReadOnlyList<NodeDto> Nodes { get; init; }

    /// <summary>
    /// Spans within the document.
    /// </summary>
    public IReadOnlyList<SpanDto> Spans { get; init; } = [];

    /// <summary>
    /// Edges scoped to this document.
    /// </summary>
    public IReadOnlyList<EdgeDto> Edges { get; init; } = [];

    /// <summary>
    /// Annotations for this document.
    /// </summary>
    public IReadOnlyList<AnnotationDto> Annotations { get; init; } = [];

    /// <summary>
    /// Annotation source identifiers.
    /// </summary>
    public IReadOnlyList<string> AnnotationSources { get; init; } = [];
}

/// <summary>
/// Serializable artifact DTO. All nullable/complex types use simple JSON-friendly representations.
/// </summary>
public sealed class ArtifactDto
{
    public required Guid Id { get; init; }
    public required string Digest { get; init; }
    public long Size { get; init; }
    public string? MediaType { get; init; }
    public string? Text { get; init; }
    public string? StoreUri { get; init; }
    public string? Headline { get; init; }
    public string? Summary { get; init; }
    public string? Structure { get; init; }
    public int? TokenCount { get; init; }
}

/// <summary>
/// Serializable node DTO. Props serialized as a JSON string.
/// </summary>
public sealed class NodeDto
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public string? Uri { get; init; }
    public Guid? ArtifactId { get; init; }
    public Guid? SpanId { get; init; }
    public string? Props { get; init; }
    public string? Headline { get; init; }
    public string? Structure { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Serializable span DTO.
/// </summary>
public sealed class SpanDto
{
    public required Guid Id { get; init; }
    public required Guid DocumentId { get; init; }
    public long? StartByte { get; init; }
    public long? EndByte { get; init; }
    public int? StartLine { get; init; }
    public int? StartColumn { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}

/// <summary>
/// Serializable edge DTO. Props serialized as a JSON string.
/// </summary>
public sealed class EdgeDto
{
    public required Guid Id { get; init; }
    public required Guid SrcId { get; init; }
    public Guid? DstId { get; init; }
    public string? DstUri { get; init; }
    public required string Type { get; init; }
    public bool IsComposition { get; init; }
    public int? Ordinal { get; init; }
    public Guid? ScopeDocumentId { get; init; }
    public string? EdgeKey { get; init; }
    public Guid? SrcSpanId { get; init; }
    public Guid? DstSpanId { get; init; }
    public string? Props { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Serializable annotation DTO. Data serialized as a JSON string.
/// </summary>
public sealed class AnnotationDto
{
    public required Guid Id { get; init; }
    public string? SemanticKey { get; init; }
    public required string Kind { get; init; }
    public required string Severity { get; init; }
    public required string Source { get; init; }
    public string? RuleId { get; init; }
    public required string Message { get; init; }
    public string? Data { get; init; }
    public required Guid ScopeDocumentId { get; init; }
    public Guid? TargetNodeId { get; init; }
    public Guid? TargetEdgeId { get; init; }
    public Guid? TargetSpanId { get; init; }
    public string? TargetUri { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
