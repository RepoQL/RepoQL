using RepoQL.Contracts;

namespace RepoQL.Formats.Json;

/// <summary>
/// Semantic media types used by the JSON format.
///
/// Purpose: Provides a single shared media type identity for generic JSON indexing.
///
/// Complexity: None. Constant declarations only.
/// </summary>
public static class JsonMediaTypes
{
    public static SemanticMediaType Json { get; } =
        SemanticMediaType.Create("application", "json").WithKind("json");
}
