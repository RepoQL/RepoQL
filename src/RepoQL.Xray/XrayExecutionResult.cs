namespace RepoQL.Xray;

/// <summary>
/// Result of an xray operation, containing both rendered output and structured results.
/// </summary>
/// <param name="RenderedOutput">Pre-rendered markdown output for display.</param>
/// <param name="Results">Structured results for programmatic use.</param>
/// <param name="Truncated">True if results were truncated due to token budget.</param>
public sealed record XrayExecutionResult(
    string RenderedOutput,
    IReadOnlyList<XrayResult> Results,
    bool Truncated);
