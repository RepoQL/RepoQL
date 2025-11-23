namespace RepoQL.Web.Services;

internal sealed record AnnotationStat(
    string RuleId,
    string Severity,
    long Count,
    long FileCount);
