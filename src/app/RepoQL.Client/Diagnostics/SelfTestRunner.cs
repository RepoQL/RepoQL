namespace RepoQL.Client.Diagnostics;

/// <summary>
/// Purpose: Provide a single entry point to render full diagnostics output on demand.
/// Complexity: Delegates to the diagnostics collector while preserving a simple async surface.
/// </summary>
public sealed class SelfTestRunner(DiagnosticsCollector collector)
{
    private readonly DiagnosticsCollector _collector = collector ?? throw new ArgumentNullException(nameof(collector));

    /// <summary>
    /// Run all diagnostic checks and return a plain-text report.
    /// All checks run regardless of earlier failures.
    /// </summary>
    public async Task<string> RunAsync(DiagnosticCollectionMode mode = DiagnosticCollectionMode.Full, CancellationToken ct = default)
    {
        var report = await _collector.CollectAsync(mode, ct).ConfigureAwait(false);
        return report.ToString();
    }
}
