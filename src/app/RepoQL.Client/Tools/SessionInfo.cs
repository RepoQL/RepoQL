namespace RepoQL.ConsoleApp.Tools;

/// <summary>
/// Purpose: Identify the current MCP session with a stable ID.
/// Complexity: UUID generated once at startup. Singleton per process lifetime.
/// </summary>
internal sealed class SessionInfo
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
}
