using RepoQL.Contracts;

namespace RepoQL.Formats.PHP;

internal static class PHPMediaTypes
{
    public static readonly SemanticMediaType PHP =
        SemanticMediaType.Create("text", "x-php").WithKind("code.php");

    public static readonly SemanticMediaType PHPTemplate =
        SemanticMediaType.Create("text", "x-php").WithKind("code.php.template");

    public static bool TryResolve(string extension, out SemanticMediaType? mediaType)
    {
        mediaType = extension switch
        {
            ".php" => PHP,
            ".phtml" => PHPTemplate,
            ".php3" => PHP,
            ".php4" => PHP,
            ".php5" => PHP,
            ".php7" => PHP,
            ".phps" => PHP,
            ".inc" => PHP,
            _ => null
        };

        return mediaType is not null;
    }
}
