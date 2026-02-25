using RepoQL.Contracts;

namespace RepoQL.Formats.Cpp;

internal static class CppMediaTypes
{
    public static readonly SemanticMediaType C =
        SemanticMediaType.Create("text", "plain").WithKind("code.c");

    public static readonly SemanticMediaType Cpp =
        SemanticMediaType.Create("text", "plain").WithKind("code.cpp");

    public static readonly SemanticMediaType CppHeader =
        SemanticMediaType.Create("text", "plain").WithKind("code.cpp-header");

    public static readonly SemanticMediaType CppInline =
        SemanticMediaType.Create("text", "plain").WithKind("code.cpp-inline");

    public static bool IsSupportedKind(string? kind)
    {
        return string.Equals(kind, C.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, Cpp.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, CppHeader.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, CppInline.Kind, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCppFamilyKind(string? kind)
    {
        return string.Equals(kind, Cpp.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, CppHeader.Kind, StringComparison.OrdinalIgnoreCase)
               || string.Equals(kind, CppInline.Kind, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryResolve(string fileName, out SemanticMediaType? mediaType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var extension = Path.GetExtension(fileName).ToUpperInvariant();
        mediaType = extension switch
        {
            ".C" => C,
            ".H" => C,
            ".CPP" => Cpp,
            ".CC" => Cpp,
            ".CXX" => Cpp,
            ".HPP" => CppHeader,
            ".HH" => CppHeader,
            ".HXX" => CppHeader,
            ".IPP" => CppInline,
            ".TPP" => CppInline,
            ".INL" => CppInline,
            _ => null
        };

        return mediaType is not null;
    }
}
