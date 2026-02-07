namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Normalize Aspire command outcomes for harness lifecycle operations.
/// Complexity: Simple success/error carrier to keep command handling uniform.
/// </summary>
internal sealed record AspireCommandResult(bool Success, string? Message, string? Error)
{
    public static AspireCommandResult Ok(string? message)
        => new(true, message, null);

    public static AspireCommandResult Fail(string error)
        => new(false, null, error);
}
