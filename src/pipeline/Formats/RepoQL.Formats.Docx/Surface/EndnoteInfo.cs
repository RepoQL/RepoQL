namespace RepoQL.Formats.Docx.Surface;

internal sealed record EndnoteInfo
{
    public required string Id { get; init; }
    public required string Text { get; init; }
}
