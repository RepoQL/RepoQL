using RepoQL.Contracts;

namespace RepoQL.Formats.Markdown;

internal sealed record CodeBlockInfo(Guid NodeId, Guid SpanId, string Language, bool IsFenced, int LineCount, string Info, DocumentSpan Span);