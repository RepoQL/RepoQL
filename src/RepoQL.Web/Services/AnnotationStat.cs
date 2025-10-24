namespace RepoQL.Web.Services;

public sealed record AnnotationStat(
    string RuleId,
    string Severity,
    long Count,
    long FileCount);