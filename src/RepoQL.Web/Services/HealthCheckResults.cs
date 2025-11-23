namespace RepoQL.Web.Services;

internal sealed record HealthCheckResults(IReadOnlyList<HealthCheck> Checks);
