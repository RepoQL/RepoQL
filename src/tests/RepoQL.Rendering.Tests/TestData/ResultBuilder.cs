using RepoQL.Xray;

namespace RepoQL.Rendering.Tests.TestData;

/// <summary>
/// Helper to create synthetic XrayResults for testing.
/// Uses controlled string lengths for predictable token estimation.
/// </summary>
public static class ResultBuilder
{
    /// <summary>
    /// Create a synthetic XrayResult with controlled field lengths.
    /// </summary>
    /// <param name="confidence">Confidence score 1-100.</param>
    /// <param name="headlineLength">Length of headline string.</param>
    /// <param name="structureLength">Length of structure string, or null for no structure.</param>
    /// <param name="snippetLength">Length of snippet string, or null for no snippet.</param>
    /// <param name="kind">Kind badge for objects, or null for documents.</param>
    /// <param name="uri">Custom URI, or auto-generated if null.</param>
    /// <param name="semanticType">Semantic type for truncation breakdown (e.g., "code.csharp").</param>
    public static XrayResult Create(
        int confidence,
        int headlineLength = 50,
        int? structureLength = null,
        int? snippetLength = null,
        string? kind = null,
        string? uri = null,
        string? semanticType = null)
    {
        return new XrayResult(
            Uri: uri ?? $"file:///test/file{confidence}.cs",
            Confidence: confidence,
            Kind: kind,
            Headline: headlineLength > 0 ? new string('h', headlineLength) : null,
            Structure: structureLength.HasValue ? new string('s', structureLength.Value) : null,
            Snippet: snippetLength.HasValue ? new string('c', snippetLength.Value) : null,
            Lang: snippetLength.HasValue ? "csharp" : null,
            SemanticType: semanticType
        );
    }

    /// <summary>
    /// Create a document result (no kind badge).
    /// </summary>
    public static XrayResult Document(int confidence, int headlineLength = 50, int? structureLength = null)
        => Create(confidence, headlineLength, structureLength, snippetLength: null, kind: null);

    /// <summary>
    /// Create an object result (with kind badge and snippet).
    /// </summary>
    public static XrayResult ObjectResult(int confidence, string kind = "method", int snippetLength = 200)
        => Create(confidence, headlineLength: 50, structureLength: null, snippetLength: snippetLength, kind: kind);

    /// <summary>
    /// Create a document result with child objects.
    /// </summary>
    public static XrayResult DocumentWithChildren(
        int confidence,
        int childCount,
        int headlineLength = 50,
        int childHeadlineLength = 30)
    {
        var children = Enumerable.Range(1, childCount)
            .Select(i => Create(confidence - i * 5, childHeadlineLength, kind: "method", uri: $"file:///test/file{confidence}.cs#method{i}"))
            .ToList();

        return new XrayResult(
            Uri: $"file:///test/file{confidence}.cs",
            Confidence: confidence,
            Kind: null,
            Headline: headlineLength > 0 ? new string('h', headlineLength) : null,
            Structure: null,
            Snippet: null,
            Lang: null,
            SemanticType: null,
            ChildObjects: children
        );
    }
}
