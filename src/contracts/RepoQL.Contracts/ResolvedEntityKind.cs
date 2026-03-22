namespace RepoQL.Contracts;

/// <summary>
///     Result row for the universal URI resolver. Entity is one of: document, edge, span.
/// </summary>
/// <summary>
/// Kinds of entities returned by the URI resolver.
/// </summary>
public enum ResolvedEntityKind
{
    /// <summary>Top-level document node (container URI).</summary>
    Document,

    /// <summary>Edge (relationship) scoped to a document.</summary>
    Edge,

    /// <summary>Span (text/byte extent) inside a document.</summary>
    Span,

    /// <summary>Unknown or other kind.</summary>
    Unknown
}