namespace RepoQL.Formats.Docx.Surface;

internal sealed record HyperlinkInfo
{
    public required string DisplayText { get; init; }
    public string? TargetUrl { get; init; }
    public required bool IsExternal { get; init; }
    public string? BookmarkName { get; init; }
}
