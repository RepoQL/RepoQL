using RepoQL.Contracts;

namespace RepoQL.Formats.CSS;

public static class CSSMediaTypes
{
    public static readonly SemanticMediaType CSS =
        SemanticMediaType.Create("text", "css").WithKind("code.css");

    public static readonly SemanticMediaType SCSS =
        SemanticMediaType.Create("text", "x-scss").WithKind("code.scss");

    public static readonly SemanticMediaType LESS =
        SemanticMediaType.Create("text", "x-less").WithKind("code.less");

    public static bool TryResolve(string extension, out SemanticMediaType? mediaType)
    {
        mediaType = extension.ToLowerInvariant() switch
        {
            ".css" => CSS,
            ".scss" => SCSS,
            ".less" => LESS,
            _ => null
        };
        return mediaType is not null;
    }

    public static bool IsSCSS(SemanticMediaType? mediaType) =>
        mediaType?.Kind == "code.scss";

    public static bool IsCSS(SemanticMediaType? mediaType) =>
        mediaType?.Kind is "code.css" or "code.less";
}
