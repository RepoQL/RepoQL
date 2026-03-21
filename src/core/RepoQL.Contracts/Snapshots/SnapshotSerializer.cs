using System.Text.Json;
using System.Text.Json.Nodes;
using RepoQL.Contracts.Models;

namespace RepoQL.Contracts.Snapshots;

/// <summary>
/// Serializes and deserializes <see cref="SnapshotManifest"/> to/from JSON.
/// Converts between domain model types and their DTO representations.
/// Invalid data fails loudly — a corrupt snapshot must never silently load.
/// </summary>
public static class SnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serialize a manifest to JSON.
    /// </summary>
    public static string Serialize(SnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    /// <summary>
    /// Serialize a manifest to a stream.
    /// </summary>
    public static void Serialize(Stream stream, SnapshotManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(manifest);
        JsonSerializer.Serialize(stream, manifest, JsonOptions);
    }

    /// <summary>
    /// Deserialize a manifest from JSON.
    /// </summary>
    /// <exception cref="JsonException">Thrown if the JSON is malformed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if required fields are missing.</exception>
    public static SnapshotManifest Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Snapshot JSON cannot be empty.", nameof(json));

        return JsonSerializer.Deserialize<SnapshotManifest>(json, JsonOptions)
               ?? throw new InvalidOperationException("Deserialized snapshot manifest was null.");
    }

    /// <summary>
    /// Deserialize a manifest from a stream.
    /// </summary>
    public static SnapshotManifest Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return JsonSerializer.Deserialize<SnapshotManifest>(stream, JsonOptions)
               ?? throw new InvalidOperationException("Deserialized snapshot manifest was null.");
    }

    /// <summary>
    /// Convert domain <see cref="Records"/> to a serializable <see cref="SnapshotDocumentDto"/>.
    /// </summary>
    public static SnapshotDocumentDto ToDto(RepoUri uri, Records records)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(records);

        return new SnapshotDocumentDto
        {
            Uri = uri.AbsoluteUri,
            Artifact = ToArtifactDto(records.Artifacts.First()),
            Nodes = records.Nodes.Select(ToNodeDto).ToList(),
            Spans = records.Spans.Select(ToSpanDto).ToList(),
            Edges = records.Edges.Select(ToEdgeDto).ToList(),
            Annotations = records.Annotations.Select(ToAnnotationDto).ToList(),
            AnnotationSources = records.AnnotationSources.ToList()
        };
    }

    /// <summary>
    /// Convert a serialized <see cref="SnapshotDocumentDto"/> back to a <see cref="SnapshotDocument"/>.
    /// </summary>
    /// <exception cref="FormatException">Thrown if a URI or media type cannot be parsed.</exception>
    public static SnapshotDocument FromDto(SnapshotDocumentDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        if (!RepoUri.TryParse(dto.Uri, out var uri))
            throw new FormatException($"Invalid document URI in snapshot: '{dto.Uri}'");

        var records = new Records
        {
            Artifacts = [FromArtifactDto(dto.Artifact)],
            Nodes = dto.Nodes.Select(FromNodeDto).ToArray(),
            Spans = dto.Spans.Select(FromSpanDto).ToArray(),
            Edges = dto.Edges.Select(FromEdgeDto).ToArray(),
            Annotations = dto.Annotations.Select(FromAnnotationDto).ToArray(),
            AnnotationSources = dto.AnnotationSources.ToArray()
        };

        return new SnapshotDocument
        {
            Uri = uri,
            Records = records
        };
    }

    // ---- Artifact ----

    private static ArtifactDto ToArtifactDto(Artifact a) => new()
    {
        Id = a.Id,
        Digest = a.Digest,
        Size = a.Size,
        MediaType = a.MediaType?.ToString(),
        Text = a.Text,
        StoreUri = a.StoreUri?.AbsoluteUri,
        Headline = a.Headline,
        Summary = a.Summary,
        Structure = a.Structure,
        TokenCount = a.TokenCount
    };

    private static Artifact FromArtifactDto(ArtifactDto d)
    {
        SemanticMediaType? mediaType = null;
        if (d.MediaType is not null && !SemanticMediaType.TryParse(d.MediaType, out mediaType))
            throw new FormatException($"Invalid media type in snapshot artifact: '{d.MediaType}'");

        RepoUri? storeUri = null;
        if (d.StoreUri is not null && !RepoUri.TryParse(d.StoreUri, out storeUri))
            throw new FormatException($"Invalid store URI in snapshot artifact: '{d.StoreUri}'");

        return new Artifact
        {
            Id = d.Id,
            Digest = d.Digest,
            Size = d.Size,
            MediaType = mediaType,
            Text = d.Text,
            StoreUri = storeUri,
            Headline = d.Headline,
            Summary = d.Summary,
            Structure = d.Structure,
            TokenCount = d.TokenCount
        };
    }

    // ---- Node ----

    private static NodeDto ToNodeDto(Node n) => new()
    {
        Id = n.Id,
        Kind = n.Kind,
        Uri = n.Uri?.AbsoluteUri,
        ArtifactId = n.ArtifactId,
        SpanId = n.SpanId,
        Props = SerializeJsonObject(n.Props),
        Headline = n.Headline,
        Structure = n.Structure,
        CreatedAt = n.CreatedAt,
        UpdatedAt = n.UpdatedAt
    };

    private static Node FromNodeDto(NodeDto d)
    {
        RepoUri? uri = null;
        if (d.Uri is not null && !RepoUri.TryParse(d.Uri, out uri))
            throw new FormatException($"Invalid node URI in snapshot: '{d.Uri}'");

        return new Node
        {
            Id = d.Id,
            Kind = d.Kind,
            Uri = uri,
            ArtifactId = d.ArtifactId,
            SpanId = d.SpanId,
            Props = DeserializeJsonObject(d.Props),
            Headline = d.Headline,
            Structure = d.Structure,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt
        };
    }

    // ---- Span ----

    private static SpanDto ToSpanDto(Span s) => new()
    {
        Id = s.Id,
        DocumentId = s.DocumentId,
        StartByte = s.StartByte,
        EndByte = s.EndByte,
        StartLine = s.StartLine,
        StartColumn = s.StartColumn,
        EndLine = s.EndLine,
        EndColumn = s.EndColumn
    };

    private static Span FromSpanDto(SpanDto d) => new()
    {
        Id = d.Id,
        DocumentId = d.DocumentId,
        StartByte = d.StartByte,
        EndByte = d.EndByte,
        StartLine = d.StartLine,
        StartColumn = d.StartColumn,
        EndLine = d.EndLine,
        EndColumn = d.EndColumn
    };

    // ---- Edge ----

    private static EdgeDto ToEdgeDto(Edge e) => new()
    {
        Id = e.Id,
        SrcId = e.SrcId,
        DstId = e.DstId,
        DstUri = e.DstUri?.AbsoluteUri,
        Type = e.Type,
        IsComposition = e.IsComposition,
        Ordinal = e.Ordinal,
        ScopeDocumentId = e.ScopeDocumentId,
        EdgeKey = e.EdgeKey,
        SrcSpanId = e.SrcSpanId,
        DstSpanId = e.DstSpanId,
        Props = SerializeJsonObject(e.Props),
        CreatedAt = e.CreatedAt
    };

    private static Edge FromEdgeDto(EdgeDto d)
    {
        RepoUri? dstUri = null;
        if (d.DstUri is not null && !RepoUri.TryParse(d.DstUri, out dstUri))
            throw new FormatException($"Invalid edge destination URI in snapshot: '{d.DstUri}'");

        return new Edge
        {
            Id = d.Id,
            SrcId = d.SrcId,
            DstId = d.DstId,
            DstUri = dstUri,
            Type = d.Type,
            IsComposition = d.IsComposition,
            Ordinal = d.Ordinal,
            ScopeDocumentId = d.ScopeDocumentId,
            EdgeKey = d.EdgeKey,
            SrcSpanId = d.SrcSpanId,
            DstSpanId = d.DstSpanId,
            Props = DeserializeJsonObject(d.Props),
            CreatedAt = d.CreatedAt
        };
    }

    // ---- Annotation ----

    private static AnnotationDto ToAnnotationDto(Annotation a) => new()
    {
        Id = a.Id,
        SemanticKey = a.SemanticKey,
        Kind = a.Kind,
        Severity = a.Severity,
        Source = a.Source,
        RuleId = a.RuleId,
        Message = a.Message,
        Data = SerializeJsonObject(a.Data),
        ScopeDocumentId = a.ScopeDocumentId,
        TargetNodeId = a.TargetNodeId,
        TargetEdgeId = a.TargetEdgeId,
        TargetSpanId = a.TargetSpanId,
        TargetUri = a.TargetUri?.AbsoluteUri,
        CreatedAt = a.CreatedAt,
        ExpiresAt = a.ExpiresAt
    };

    private static Annotation FromAnnotationDto(AnnotationDto d)
    {
        RepoUri? targetUri = null;
        if (d.TargetUri is not null && !RepoUri.TryParse(d.TargetUri, out targetUri))
            throw new FormatException($"Invalid annotation target URI in snapshot: '{d.TargetUri}'");

        return new Annotation
        {
            Id = d.Id,
            SemanticKey = d.SemanticKey,
            Kind = d.Kind,
            Severity = d.Severity,
            Source = d.Source,
            RuleId = d.RuleId,
            Message = d.Message,
            Data = DeserializeJsonObject(d.Data),
            ScopeDocumentId = d.ScopeDocumentId,
            TargetNodeId = d.TargetNodeId,
            TargetEdgeId = d.TargetEdgeId,
            TargetSpanId = d.TargetSpanId,
            TargetUri = targetUri,
            CreatedAt = d.CreatedAt,
            ExpiresAt = d.ExpiresAt
        };
    }

    // ---- JsonObject helpers ----

    private static string? SerializeJsonObject(JsonObject? obj)
    {
        if (obj is null || obj.Count == 0) return null;
        return obj.ToJsonString();
    }

    private static JsonObject DeserializeJsonObject(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new JsonObject();
        var node = JsonNode.Parse(json);
        return node?.AsObject() ?? new JsonObject();
    }
}
