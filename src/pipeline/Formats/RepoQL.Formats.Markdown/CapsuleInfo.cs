using RepoQL.Contracts;

namespace RepoQL.Formats.Markdown;

/// <summary>
/// Represents a parsed Knowledge Capsule from a markdown document.
///
/// Purpose: Captures structured knowledge in the Invariant-Example-Depth format,
/// enabling rich indexing and cross-referencing of conceptual documentation.
///
/// Complexity: Extracts and normalizes three distinct sections from markdown AST,
/// parsing SeeAlso references for graph edges. The rest of the system receives
/// clean, structured data without needing to understand capsule parsing.
/// </summary>
internal sealed record CapsuleInfo(
    Guid NodeId,
    Guid SpanId,
    string Name,
    string Invariant,
    string? Example,
    bool HasBoundary,
    string? BoundaryText,
    IReadOnlyList<string> SeeAlso,
    int HeadingLevel,
    DocumentSpan CapsuleSpan);
