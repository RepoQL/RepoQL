namespace RepoQL.Formats.Docx.Surface;

internal sealed record DocumentStats
{
    public int? PageCount { get; init; }
    public int? WordCount { get; init; }
    public int ParagraphCount { get; init; }
}
