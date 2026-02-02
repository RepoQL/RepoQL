namespace RepoQL.ConsoleApp.Tools;

/// <summary>
/// Tracks whether the agent has oriented themselves by reading the help documentation.
///
/// Purpose: Nudge agents to read the help before using RepoQL tools. The first call
/// that isn't reading help returns a gentle reminder instead of executing.
///
/// Complexity: Simple boolean state. Singleton per MCP session. Thread-safe via volatile.
/// </summary>
internal sealed class SessionOrientation
{
    private volatile bool _hasReadHelp;
    private volatile bool _hasBeenNudged;

    /// <summary>
    /// Check if the agent should be nudged to read help first.
    /// Returns the nudge message if needed, null if oriented.
    /// </summary>
    public string? CheckOrientation(string toolName, string? uri)
    {
        // Already oriented? Proceed.
        if (_hasReadHelp)
            return null;

        // Is this the help read? Mark oriented and proceed.
        if (IsHelpRead(uri))
        {
            _hasReadHelp = true;
            return null;
        }

        // First non-help call: nudge once, then let them proceed
        if (!_hasBeenNudged)
        {
            _hasBeenNudged = true;
            return "What were you supposed to do first?";
        }

        // Already nudged once, let them proceed (don't block forever)
        return null;
    }

    private static bool IsHelpRead(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return false;

        // Accept any help:// read as orientation
        return uri.StartsWith("help://", StringComparison.OrdinalIgnoreCase);
    }
}
