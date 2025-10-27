using Microsoft.CodeAnalysis.Text;
using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

internal static class CSharpIdFactory
{
    public static Guid CreateDocumentId(RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return DeterministicGuid.Create("csharp.document", uri.ToString().ToLowerInvariant());
    }

    public static Guid CreateNodeId(Guid documentId, string category, TextSpan span)
    {
        return DeterministicGuid.Create(
            "csharp.node",
            documentId.ToString("N"),
            category,
            span.Start.ToString(),
            span.Length.ToString());
    }

    public static Guid CreateSpanId(Guid documentId, string category, TextSpan span)
    {
        return DeterministicGuid.Create(
            "csharp.span",
            documentId.ToString("N"),
            category,
            span.Start.ToString(),
            span.Length.ToString());
    }
}
