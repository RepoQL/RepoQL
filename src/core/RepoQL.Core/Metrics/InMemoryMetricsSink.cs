using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace RepoQL.Core.Metrics;

/// <summary>
/// In-memory sink that subscribes to .NET Metrics (System.Diagnostics.Metrics) via MeterListener
/// and keeps simple cumulative totals per instrument name. Intended for live dashboards/tests.
/// </summary>
public sealed class InMemoryMetricsSink : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, double> _counterTotals = new(StringComparer.Ordinal);

    public InMemoryMetricsSink(params string[] meterNames)
    {
        var names = (meterNames is { Length: > 0 }) ? new HashSet<string>(meterNames) : new HashSet<string>([
            "RepoQL.Indexing"
        ]);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (names.Contains(instrument.Meter.Name))
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.SetMeasurementEventCallback<long>((inst, val, tags, s) => OnMeasurement(inst, val, tags, s));
        _listener.Start();
    }

    private void OnMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        // Treat all incoming measurements as additive totals keyed by instrument name.
        // For counters this is the cumulative count; for histograms this becomes a simple sum of observations.
        _counterTotals.AddOrUpdate(instrument.Name, value, (_, cur) => cur + value);
    }

    public double GetTotal(string instrumentName) => _counterTotals.TryGetValue(instrumentName, out var v) ? v : 0;

    public IReadOnlyDictionary<string, double> Snapshot() => new Dictionary<string, double>(_counterTotals);

    public void Dispose()
    {
        _listener.Dispose();
    }
}