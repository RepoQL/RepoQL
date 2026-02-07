namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Describes how the harness launches the RepoQL MCP subprocess.
/// Complexity: Small configuration surface kept isolated so resolution can evolve without touching proxy logic.
/// </summary>
internal sealed record RepoqlSubprocessOptions(string FileName, string Arguments, string WorkingDirectory)
{
    public static RepoqlSubprocessOptions CreateDefault()
    {
        return new RepoqlSubprocessOptions(
            FileName: "repoql",
            Arguments: "mcp",
            WorkingDirectory: Environment.CurrentDirectory);
    }
}
