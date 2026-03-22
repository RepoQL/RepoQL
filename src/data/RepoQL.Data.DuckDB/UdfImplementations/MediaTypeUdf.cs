using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDFs for semantic media type parsing and manipulation.
///
/// Purpose: Provides SQL-callable functions for extracting and modifying
/// parts of semantic media types (base type, kind, version, parameters).
///
/// Complexity: Uses SemanticMediaType parser from Contracts. Pure functions.
/// </summary>
[UdfClass]
public class MediaTypeUdf
{
    /// <summary>
    /// Extracts the base media type (type/subtype) without parameters.
    /// Example: "text/x-csharp; kind=class" → "text/x-csharp"
    /// </summary>
    [ScalarUdf("media_type_base", IsPure = true)]
    public string? GetBase(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return null;

        if (!SemanticMediaType.TryParse(mediaType, out var mt))
            return null;

        return $"{mt!.Type}/{mt.Subtype}";
    }

    /// <summary>
    /// Extracts the 'kind' parameter from a semantic media type.
    /// Example: "text/x-csharp; kind=class" → "class"
    /// </summary>
    [ScalarUdf("media_type_kind", IsPure = true)]
    public string? GetKind(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return null;

        if (!SemanticMediaType.TryParse(mediaType, out var mt) || mt!.Kind is null)
            return null;

        return mt.Kind;
    }

    /// <summary>
    /// Extracts the 'version' parameter from a semantic media type.
    /// Example: "application/json; version=2.0" → "2.0"
    /// </summary>
    [ScalarUdf("media_type_version", IsPure = true)]
    public string? GetVersion(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return null;

        if (!SemanticMediaType.TryParse(mediaType, out var mt) || mt!.Version is null)
            return null;

        return mt.Version;
    }

    /// <summary>
    /// Adds or updates a parameter in a media type.
    /// Example: ("text/plain", "charset", "utf-8") → "text/plain; charset=utf-8"
    /// </summary>
    [ScalarUdf("media_type_with_parameter", IsPure = true)]
    public string? WithParameter(string? mediaType, string? key, [UdfDefault("NULL")] string? value)
    {
        if (string.IsNullOrWhiteSpace(mediaType) || string.IsNullOrWhiteSpace(key))
            return null;

        if (!SemanticMediaType.TryParse(mediaType, out var mt))
            return null;

        return mt!.With(key, value).ToString();
    }
}
