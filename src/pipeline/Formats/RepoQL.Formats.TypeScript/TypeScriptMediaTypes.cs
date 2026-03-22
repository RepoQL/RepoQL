using RepoQL.Contracts;

namespace RepoQL.Formats.TypeScript;

internal static class TypeScriptMediaTypes
{
    public static readonly SemanticMediaType TypeScript =
        SemanticMediaType.Create("text", "x-typescript").WithKind("code.typescript");

    public static readonly SemanticMediaType TypeScriptReact =
        SemanticMediaType.Create("text", "x-typescript").WithKind("code.typescript.react");

    public static readonly SemanticMediaType JavaScript =
        SemanticMediaType.Create("text", "javascript").WithKind("code.javascript");

    public static readonly SemanticMediaType JavaScriptReact =
        SemanticMediaType.Create("text", "javascript").WithKind("code.javascript.react");

    public static bool TryResolve(string extension, out SemanticMediaType? mediaType)
    {
        mediaType = extension switch
        {
            ".ts" => TypeScript,
            ".tsx" => TypeScriptReact,
            ".js" => JavaScript,
            ".jsx" => JavaScriptReact,
            _ => null
        };

        return mediaType is not null;
    }
}
