namespace RepoQL.Testing;

/// <summary>
/// Skips tests when running in CI (GitHub Actions sets CI=true).
/// Use for timing-sensitive tests that rely on fast runner performance.
/// </summary>
internal sealed class SkipOnCIAttribute() : SkipAttribute("Timing-sensitive: skipped on CI runners")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(Environment.GetEnvironmentVariable("CI") == "true");
        
}