namespace RepoQL.Cli.Tests;

/// <summary>
/// Purpose: Prevent RepoQL host process launch during tests.
/// Complexity: Without this, tests that reach the connection path spawn a real
/// <c>dotnet watch run</c> process (in DEBUG builds) via <c>LaunchHost</c>.
/// The process and its stdout/stderr streaming Task.Run loops persist as static state,
/// preventing TUnit from exiting after all tests complete.
/// </summary>
internal static class GlobalSetup
{
    [Before(TestSession)]
    public static void SuppressHostLaunch()
    {
        Environment.SetEnvironmentVariable("REPOQL_SUPPRESS_HOST_LAUNCH", "1");
    }

    [After(TestSession)]
    public static void RestoreHostLaunch()
    {
        Environment.SetEnvironmentVariable("REPOQL_SUPPRESS_HOST_LAUNCH", null);
    }
}
