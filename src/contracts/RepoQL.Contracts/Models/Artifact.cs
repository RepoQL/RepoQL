using System.Diagnostics;

namespace RepoQL.Contracts.Models;

/// <summary>
///     Content-addressed bytes that may be referenced by one or more document nodes.
/// </summary>
[DebuggerDisplay("{Digest} ({Size} bytes)")]
public sealed record Artifact
{
    /// <summary>
    ///     Gets the stable identifier for this artifact. Value is a generated Guid.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    ///     Gets the content digest of the raw bytes, for example <c>xxh64:&lt;hex&gt;</c> or
    ///     <c>xxh64-sampled:v1:&lt;hex&gt;</c> for large-file sampled digests.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>
    ///     Gets the uncompressed size of the artifact in bytes.
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    ///     Gets the semantic media type that describes wire format and representation semantics.
    /// </summary>
    public SemanticMediaType? MediaType { get; init; }

    /// <summary>
    ///     Gets the optional decoded text for small artifacts to support span mapping and text search.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    ///     Gets an optional external location where the bytes are stored, for example a file path or object store URI.
    /// </summary>
    public RepoUri? StoreUri { get; init; }

    /// <summary>
    ///     X-ray Headline (Level 0): essential identity in a single line. Producers should always populate this for documents.
    /// </summary>
    public string? Headline { get; init; }

    /// <summary>
    ///     X-ray Summary (Level 1): key information (~5 lines, max 10), enabling understanding without reading full content.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    ///     X-ray Structure (Level 2): detailed outline (~15 lines, max 25) for navigation and structural exploration.
    /// </summary>
    public string? Structure { get; init; }

    /// <summary>
    ///     Estimated token count for the text content using Claude BPE tokenizer.
    ///     NULL for binary files or if estimation failed.
    /// </summary>
    public int? TokenCount { get; init; }
}
