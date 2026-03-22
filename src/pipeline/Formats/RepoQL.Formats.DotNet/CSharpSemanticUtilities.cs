using Microsoft.CodeAnalysis;

namespace RepoQL.Formats.DotNet;

internal static class CSharpSemanticUtilities
{
    public static string BuildSymbolKey(ISymbol symbol)
    {
        try
        {
            return symbol.GetDocumentationCommentId()
                   ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
        catch
        {
            // GetDocumentationCommentId can throw NullReferenceException for certain symbols
            // (e.g., type parameters in XML documentation comments)
            return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }
}
