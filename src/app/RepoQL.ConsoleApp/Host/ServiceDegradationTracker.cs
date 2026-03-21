using Grpc.Health.V1;
using Grpc.HealthCheck;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Host;

/// <summary>
/// Purpose: Track degradations, update health checks, and persist diagnostics.
/// Complexity: Centralizes sticky degradation state and health status updates.
/// </summary>
internal sealed class ServiceDegradationTracker : IServiceDegradationTracker
{
    private readonly HostState _hostState;
    private readonly string _repoRoot;
    private HealthServiceImpl? _health;

    public ServiceDegradationTracker(HostState hostState, string repoRoot)
    {
        _hostState = hostState ?? throw new ArgumentNullException(nameof(hostState));
        _repoRoot = repoRoot ?? throw new ArgumentNullException(nameof(repoRoot));
    }

    public IReadOnlyList<ServiceDegradationEntry> Entries => _hostState.Degradation.Entries;

    public void AttachHealth(HealthServiceImpl health)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));

        foreach (var kind in Enum.GetValues<ServiceDegradationKind>())
        {
            _health.SetStatus(ToHealthName(kind), HealthCheckResponse.Types.ServingStatus.Serving);
        }

        foreach (var entry in Entries)
        {
            _health.SetStatus(ToHealthName(entry.Kind), HealthCheckResponse.Types.ServingStatus.NotServing);
        }

        WriteReport();
    }

    public void MarkDegraded(ServiceDegradationKind kind, string message)
    {
        if (!_hostState.Degradation.MarkDegraded(kind, message))
            return;

        _health?.SetStatus(ToHealthName(kind), HealthCheckResponse.Types.ServingStatus.NotServing);
        WriteReport();
    }

    public void WriteReport()
    {
        HostDiagnosticsStore.TryWriteReport(_repoRoot, "services-start.json", BuildReport());
    }

    private ServicesStartReport BuildReport()
    {
        var report = new ServicesStartReport();
        foreach (var entry in Entries)
        {
            report.AddIssue(entry.Kind, entry.Message);
        }

        return report;
    }

    private static string ToHealthName(ServiceDegradationKind kind)
        => kind switch
        {
            ServiceDegradationKind.Embeddings => "repoql.embeddings",
            ServiceDegradationKind.Mcp => "repoql.mcp",
            ServiceDegradationKind.Mounts => "repoql.mounts",
            ServiceDegradationKind.Indexer => "repoql.indexer",
            ServiceDegradationKind.Watcher => "repoql.watcher",
            _ => "repoql.unknown"
        };
}
