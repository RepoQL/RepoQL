using Microsoft.CodeAnalysis;

namespace RepoQL.Formats.DotNet;

internal static class CSharpSemanticUtilities
{
    public static string BuildSymbolKey(ISymbol symbol)
        => symbol.GetDocumentationCommentId()
           ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

}
