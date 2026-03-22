using RepoQL.Contracts;
using RepoQL.Contracts.Models;

namespace RepoQL.Formats.GraphQL;

internal static class GraphQLSpanExtensions
{
    public static DocumentSpan ToDocumentSpan(this GraphQLSpan span, DocumentModel document)
    {
        var start = Clamp(span.Start, document.Text.Length);
        var end = Clamp(span.End, document.Text.Length);
        if (end < start) end = start;
        return document.LineMap.GetSpan(start, end);
    }

    public static Span ToSpan(this GraphQLSpan span, DocumentModel document, Guid documentNodeId, Guid spanId)
    {
        var docSpan = span.ToDocumentSpan(document);
        return new Span
        {
            Id = spanId,
            DocumentId = documentNodeId,
            StartLine = docSpan.StartLine,
            StartColumn = docSpan.StartColumn,
            EndLine = docSpan.EndLine,
            EndColumn = docSpan.EndColumn,
            StartByte = CalculateUtf8Bytes(document.Text, docSpan.StartChar),
            EndByte = CalculateUtf8Bytes(document.Text, docSpan.EndChar)
        };
    }

    private static int Clamp(int value, int max) => value < 0 ? 0 : value > max ? max : value;

    private static long CalculateUtf8Bytes(string text, int chars)
        => System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, Math.Min(text.Length, chars)));
}
