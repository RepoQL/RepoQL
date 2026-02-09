namespace RepoQL.Formats.Docx.Surface;

internal sealed record DocumentProperties
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? LastModifiedBy { get; init; }
    public string? Description { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Application { get; init; }
    public IReadOnlyDictionary<string, string?> CustomProperties { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}
