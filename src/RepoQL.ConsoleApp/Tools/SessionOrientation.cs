namespace RepoQL.ConsoleApp.Tools;

/// <summary>
/// Tracks whether the agent has oriented themselves by reading the help documentation.
///
/// Purpose: Append a reminder to tool responses until the agent reads help://.
/// Never blocks — the request always executes. The reminder disappears once oriented.
///
/// Complexity: Simple boolean state. Singleton per MCP session. Thread-safe via volatile.
/// </summary>
internal sealed class SessionOrientation
{
    private const string Nudge =
        "\n\n⚠️ run read(\"help://** => tree: headlines\", 3000) — Vital for your success";

    private volatile bool _hasReadHelp;

    /// <summary>
    /// Mark oriented if this is a help read. Returns a footer to append
    /// to the tool response if not yet oriented, null otherwise.
    /// </summary>
    public string? CheckOrientation(string? uri)
    {
        if (_hasReadHelp)
            return null;

        if (IsHelpRead(uri))
        {
            _hasReadHelp = true;
            return null;
        }

        return Nudge;
    }

    private static bool IsHelpRead(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return false;

        return uri.StartsWith("help://", StringComparison.OrdinalIgnoreCase);
    }
}
