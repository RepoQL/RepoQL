namespace RepoQL.Protocol;

/// <summary>
/// Purpose: Signal infrastructure failures with attached diagnostics for callers to display.
/// Complexity: Wraps an inner exception while preserving a structured diagnostics payload.
/// </summary>
public sealed class RepoQlDiagnosticsException : Exception
{
    public RepoQlDiagnosticsException()
        : this("RepoQL diagnostics failure.", RepoQlDiagnostics.Empty)
    {
    }

    public RepoQlDiagnosticsException(string message)
        : this(message, RepoQlDiagnostics.Empty)
    {
    }

    public RepoQlDiagnosticsException(string message, Exception innerException)
        : this(message, RepoQlDiagnostics.Empty, innerException)
    {
    }

    public RepoQlDiagnosticsException(string message, RepoQlDiagnostics diagnostics, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public RepoQlDiagnostics Diagnostics { get; }
}
