using Microsoft.CodeAnalysis.Text;
using RepoQL.Contracts;

namespace RepoQL.Formats.DotNet;

internal static class CSharpIdFactory
{
    public static Guid CreateDocumentId(RepoUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // Fail fast if URI is an absolute filesystem path rather than repo-relative
        // Absolute paths look like file:///C:/... or file:///D:/... on Windows
        var path = uri.GetComponents(UriComponents.Path, UriFormat.Unescaped);
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            throw new ArgumentException(
                $"URI must be repo-relative, not an absolute filesystem path. Got: {uri}",
                nameof(uri));
        }

        // Use Container.AbsoluteUri to match database storage (UpsertDocumentByUri)
        return DeterministicGuid.Create("csharp.document", uri.Container.AbsoluteUri.ToLowerInvariant());
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
