namespace RepoQL.Contracts;

/// <summary>
/// Result row for the universal URI resolver.
/// </summary>
/// <param name="Kind">Kind of resolved entity. Prefer <see cref="ResolvedEntityKind.Document"/>, <see cref="ResolvedEntityKind.Edge"/> or <see cref="ResolvedEntityKind.Span"/>.</param>
/// <param name="EntityId">Identifier of the resolved entity (node, edge or span GUID).</param>
/// <param name="RelationType">
/// For edges this is the edge type (e.g. "CALLS", "REFERS_TO").
/// For non-edge rows this is null.
/// </param>
/// <param name="ResolvedUri">Fully qualified repository URI that identifies the resolved entity (container + fragment).</param>
/// <param name="DocumentUri">Container/document URI (no fragment) that owns the resolved entity.</param>
/// <param name="Fragment">
/// Fragment or selector used to identify the sub-resource. Examples:
/// null, "edge=&lt;guid&gt;", "line=12,14", "char=102,150", "/paths/0".
/// </param>
public readonly record struct ResolvedEntity(
    ResolvedEntityKind Kind,
    Guid EntityId,
    string? RelationType,
    string ResolvedUri,
    string DocumentUri,
    string? Fragment
);