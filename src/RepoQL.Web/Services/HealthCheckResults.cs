namespace RepoQL.Web.Services;

public sealed record HealthCheckResults(IReadOnlyList<HealthCheck> Checks);