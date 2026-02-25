using RepoQL.Contracts;

namespace RepoQL.Formats.Go;

internal static class GoMediaTypes
{
    public static readonly SemanticMediaType Go =
        SemanticMediaType.Create("text", "x-go").WithKind("code.go");

    public static readonly SemanticMediaType GoMod =
        SemanticMediaType.Create("text", "x-go-mod").WithKind("code.go.mod");

    public static readonly SemanticMediaType GoWork =
        SemanticMediaType.Create("text", "x-go-work").WithKind("code.go.work");

    public static bool IsSupportedKind(string? kind) =>
        IsGoSourceKind(kind) || IsGoModuleMetadataKind(kind);

    public static bool IsGoSourceKind(string? kind) =>
        string.Equals(kind, Go.Kind, StringComparison.OrdinalIgnoreCase);

    public static bool IsGoModuleMetadataKind(string? kind) =>
        IsGoModKind(kind) || IsGoWorkKind(kind);

    public static bool IsGoModKind(string? kind) =>
        string.Equals(kind, GoMod.Kind, StringComparison.OrdinalIgnoreCase);

    public static bool IsGoWorkKind(string? kind) =>
        string.Equals(kind, GoWork.Kind, StringComparison.OrdinalIgnoreCase);

    public static bool TryResolve(string fileName, out SemanticMediaType? mediaType)
    {
        var extension = Path.GetExtension(fileName);
        mediaType = extension.ToLowerInvariant() switch
        {
            ".go" => Go,
            _ => ResolveByName(fileName)
        };
        return mediaType is not null;
    }

    private static SemanticMediaType? ResolveByName(string fileName)
    {
        return Path.GetFileName(fileName) switch
        {
            "go.mod" => GoMod,
            "go.work" => GoWork,
            _ => null
        };
    }
}
