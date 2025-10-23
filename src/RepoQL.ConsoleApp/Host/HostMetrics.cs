using System.Diagnostics.Metrics;

namespace RepoQL.ConsoleApp.Host;

internal sealed class HostMetrics : IDisposable
{
    private readonly Meter _meter;
    private Func<int> _leaseCountProvider = static () => 0;
    private Func<int> _writerPendingProvider = static () => 0;
    private Func<int> _implicitStartProvider = static () => 0;
    private Func<double> _idleSecondsProvider = static () => -1;

    public HostMetrics(string meterName = "RepoQL.Host")
    {
        _meter = new Meter(meterName);
        _meter.CreateObservableGauge(
            "repoql.host.leases.active",
            () => _leaseCountProvider(),
            unit: "count",
            description: "Active lease holders connected to the host");
        _meter.CreateObservableGauge(
            "repoql.host.writer.pending",
            () => _writerPendingProvider(),
            unit: "items",
            description: "Pending write operations");
        _meter.CreateObservableGauge(
            "repoql.host.implicit",
            () => _implicitStartProvider(),
            unit: "bool",
            description: "Host was started implicitly");
        _meter.CreateObservableGauge(
            "repoql.host.idle.seconds_until_shutdown",
            () => _idleSecondsProvider(),
            unit: "s",
            description: "Seconds remaining until idle shutdown");
    }

    public void SetLeaseCountProvider(Func<int> provider) => _leaseCountProvider = provider ?? (() => 0);
    public void SetWriterPendingProvider(Func<int> provider) => _writerPendingProvider = provider ?? (() => 0);
    public void SetImplicitStartProvider(Func<int> provider) => _implicitStartProvider = provider ?? (() => 0);
    public void SetIdleSecondsProvider(Func<double> provider) => _idleSecondsProvider = provider ?? (() => -1);

    public void Dispose() => _meter.Dispose();
}
