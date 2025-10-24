using RepoQL.Contracts;

namespace RepoQL.Formats.Markdown;

internal sealed record LinkInfo(Guid NodeId, Guid SpanId, string Href, string Title, string Text, DocumentSpan Span);