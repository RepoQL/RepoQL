using RepoQL.Contracts;

namespace RepoQL.Formats.Pdf;

internal static class PdfMediaTypes
{
    public static readonly SemanticMediaType Base = SemanticMediaType
        .Create("application", "pdf");

    public static readonly SemanticMediaType Document = Base.WithKind("pdf.document");

    public static readonly SemanticMediaType Form = Base.WithKind("pdf.form");

    public static readonly SemanticMediaType Scan = Base.WithKind("pdf.scan");

    public static bool IsPdf(SemanticMediaType? mediaType)
    {
        if (mediaType is null)
            return false;

        return string.Equals(mediaType.Type, "application", StringComparison.OrdinalIgnoreCase)
               && string.Equals(mediaType.Subtype, "pdf", StringComparison.OrdinalIgnoreCase);
    }
}
