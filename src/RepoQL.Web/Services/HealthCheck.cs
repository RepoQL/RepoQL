namespace RepoQL.Web.Services;

internal sealed record HealthCheck(
    HealthCheckSeverity Severity,
    string Message,
    long Count,
    IReadOnlyList<string> Samples);
