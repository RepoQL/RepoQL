namespace RepoQL.Formats.Docx.Surface;

internal sealed record HeadingInfo(
    Guid NodeId,
    Guid SpanId,
    int Level,
    string Text,
    int ParagraphIndex,
    string Symbol,
    int OutputLine,
    int StartChar);
