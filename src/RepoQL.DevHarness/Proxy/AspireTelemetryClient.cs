namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Abstract Aspire telemetry queries for logs and traces.
/// Complexity: Minimal async surface to enable test doubles.
/// </summary>
internal interface IAspireTelemetryClient
{
    Task<AspireTelemetryResult> ListStructuredLogsAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
    Task<AspireTelemetryResult> ListTracesAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
    Task<AspireTelemetryResult> ListConsoleLogsAsync(string resourceName, CancellationToken cancellationToken);
    Task<AspireTelemetryResult> ListTraceStructuredLogsAsync(string traceId, CancellationToken cancellationToken);
    Task<AspireCommandResult> ExecuteResourceCommandAsync(string resourceName, string commandName, CancellationToken cancellationToken);
}

/// <summary>
/// Purpose: Normalize Aspire telemetry tool outcomes for the harness.
/// Complexity: Simple success/error carrier with raw payload content.
/// </summary>
internal sealed record AspireTelemetryResult(bool Success, string? Content, string? Error)
{
    public static AspireTelemetryResult Ok(string? content)
        => new(true, content, null);

    public static AspireTelemetryResult Fail(string error)
        => new(false, null, error);
}
