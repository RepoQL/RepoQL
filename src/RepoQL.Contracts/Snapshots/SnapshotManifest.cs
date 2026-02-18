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
    public required string FormatVersion { get; set; }

    /// <summary>
    /// The snapshot source identifier (matches <see cref="ISnapshotSource.Id"/>).
    /// </summary>
    public required string SourceId { get; set; }

    /// <summary>
    /// The snapshot version (matches <see cref="ISnapshotSource.Version"/>).
    /// </summary>
    public required string Version { get; set; }

    /// <summary>
    /// The serialized documents.
    /// </summary>
    public required IReadOnlyList<SnapshotDocumentDto> Documents { get; set; }
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
    public required string Uri { get; set; }

    /// <summary>
    /// The artifact record.
    /// </summary>
    public required ArtifactDto Artifact { get; set; }

    /// <summary>
    /// All nodes (document node + children).
    /// </summary>
    public required IReadOnlyList<NodeDto> Nodes { get; set; }

    /// <summary>
    /// Spans within the document.
    /// </summary>
    public IReadOnlyList<SpanDto> Spans { get; set; } = [];

    /// <summary>
    /// Edges scoped to this document.
    /// </summary>
    public IReadOnlyList<EdgeDto> Edges { get; set; } = [];

    /// <summary>
    /// Annotations for this document.
    /// </summary>
    public IReadOnlyList<AnnotationDto> Annotations { get; set; } = [];

    /// <summary>
    /// Annotation source identifiers.
    /// </summary>
    public IReadOnlyList<string> AnnotationSources { get; set; } = [];
}

/// <summary>
/// Serializable artifact DTO. All nullable/complex types use simple JSON-friendly representations.
/// </summary>
public sealed class ArtifactDto
{
    public required Guid Id { get; set; }
    public required string Digest { get; set; }
    public long Size { get; set; }
    public string? MediaType { get; set; }
    public string? Text { get; set; }
    public string? StoreUri { get; set; }
    public string? Headline { get; set; }
    public string? Summary { get; set; }
    public string? Structure { get; set; }
    public int? TokenCount { get; set; }
}

/// <summary>
/// Serializable node DTO. Props serialized as a JSON string.
/// </summary>
public sealed class NodeDto
{
    public required Guid Id { get; set; }
    public required string Kind { get; set; }
    public string? Uri { get; set; }
    public Guid? ArtifactId { get; set; }
    public Guid? SpanId { get; set; }
    public string? Props { get; set; }
    public string? Headline { get; set; }
    public string? Structure { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Serializable span DTO.
/// </summary>
public sealed class SpanDto
{
    public required Guid Id { get; set; }
    public required Guid DocumentId { get; set; }
    public long? StartByte { get; set; }
    public long? EndByte { get; set; }
    public int? StartLine { get; set; }
    public int? StartColumn { get; set; }
    public int? EndLine { get; set; }
    public int? EndColumn { get; set; }
}

/// <summary>
/// Serializable edge DTO. Props serialized as a JSON string.
/// </summary>
public sealed class EdgeDto
{
    public required Guid Id { get; set; }
    public required Guid SrcId { get; set; }
    public Guid? DstId { get; set; }
    public string? DstUri { get; set; }
    public required string Type { get; set; }
    public bool IsComposition { get; set; }
    public int? Ordinal { get; set; }
    public Guid? ScopeDocumentId { get; set; }
    public string? EdgeKey { get; set; }
    public Guid? SrcSpanId { get; set; }
    public Guid? DstSpanId { get; set; }
    public string? Props { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Serializable annotation DTO. Data serialized as a JSON string.
/// </summary>
public sealed class AnnotationDto
{
    public required Guid Id { get; set; }
    public string? SemanticKey { get; set; }
    public required string Kind { get; set; }
    public required string Severity { get; set; }
    public required string Source { get; set; }
    public string? RuleId { get; set; }
    public required string Message { get; set; }
    public string? Data { get; set; }
    public required Guid ScopeDocumentId { get; set; }
    public Guid? TargetNodeId { get; set; }
    public Guid? TargetEdgeId { get; set; }
    public Guid? TargetSpanId { get; set; }
    public string? TargetUri { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
