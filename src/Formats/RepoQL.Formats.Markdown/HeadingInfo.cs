using RepoQL.Contracts;

namespace RepoQL.Formats.Markdown;

internal sealed record HeadingInfo(Guid NodeId, Guid SpanId, int Level, string Text, string Slug, DocumentSpan Span);