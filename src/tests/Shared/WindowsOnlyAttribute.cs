namespace RepoQL.Testing;

/// <summary>
/// Skips tests on non-Windows platforms.
/// Use for tests with platform-specific behavior (e.g., WASM runtime differences).
/// </summary>
internal sealed class WindowsOnlyAttribute() : SkipAttribute("Platform-specific: Windows only")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext testContext)
        => Task.FromResult(!OperatingSystem.IsWindows());
}