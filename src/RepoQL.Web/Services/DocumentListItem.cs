namespace RepoQL.Web.Services;

internal sealed record DocumentListItem(
    string DocumentUri,
    string FileName,
    string MediaLabel,
    long? ByteSize,
    string KindsSummary,
    string? Headline,
    string? Summary,
    string? Structure);
