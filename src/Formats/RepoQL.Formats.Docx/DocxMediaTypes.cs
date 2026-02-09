using RepoQL.Contracts;

namespace RepoQL.Formats.Docx;

internal static class DocxMediaTypes
{
    public static readonly SemanticMediaType Document = SemanticMediaType
        .Create("application", "docx")
        .WithKind("docx.document");

    public static readonly SemanticMediaType MacroEnabledDocument = SemanticMediaType
        .Create("application", "docm")
        .WithKind("docx.document");

    public static readonly SemanticMediaType Template = SemanticMediaType
        .Create("application", "dotx")
        .WithKind("docx.template");

    public static bool TryResolveByExtension(string extension, out SemanticMediaType? mediaType)
    {
        mediaType = extension.ToUpperInvariant() switch
        {
            ".DOCX" => Document,
            ".DOCM" => MacroEnabledDocument,
            ".DOTX" => Template,
            _ => null
        };

        return mediaType is not null;
    }

    public static bool TryResolveBySubtype(string subtype, out SemanticMediaType? mediaType)
    {
        mediaType = subtype.ToUpperInvariant() switch
        {
            "DOCX" => Document,
            "DOCM" => MacroEnabledDocument,
            "DOTX" => Template,
            _ => null
        };

        return mediaType is not null;
    }

    public static bool IsSupportedKind(string? kind)
        => string.Equals(kind, Document.Kind, StringComparison.OrdinalIgnoreCase)
           || string.Equals(kind, Template.Kind, StringComparison.OrdinalIgnoreCase);
}
