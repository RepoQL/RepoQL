namespace RepoQL.Web.Services;

public sealed record HealthCheck(
    HealthCheckSeverity Severity,
    string Message,
    long Count,
    IReadOnlyList<string> Samples);