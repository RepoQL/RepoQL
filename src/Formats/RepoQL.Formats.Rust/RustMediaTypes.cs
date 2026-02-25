using RepoQL.Contracts;

namespace RepoQL.Formats.Rust;

internal static class RustMediaTypes
{
    public static readonly SemanticMediaType Rust =
        SemanticMediaType.Create("text", "x-rust").WithKind("code.rust");

    public static readonly SemanticMediaType BuildScript =
        SemanticMediaType.Create("text", "x-rust").WithKind("code.rust.build");

    public static bool IsSupportedKind(string? kind)
    {
        return string.Equals(kind, Rust.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, BuildScript.Kind, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryResolve(string fileName, out SemanticMediaType? mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var name = Path.GetFileName(fileName);
        if (string.Equals(name, "build.rs", StringComparison.OrdinalIgnoreCase))
        {
            mediaType = BuildScript;
            return true;
        }

        var extension = Path.GetExtension(fileName);
        mediaType = string.Equals(extension, ".rs", StringComparison.OrdinalIgnoreCase)
            ? Rust
            : null;
        return mediaType is not null;
    }
}
