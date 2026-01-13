namespace RepoQL.Mcp.Client;

/// <summary>
/// OAuth credentials for an MCP server.
///
/// Purpose: Holds access/refresh tokens and metadata needed to authenticate with OAuth-protected MCP servers.
/// Complexity: Simple data record with computed properties for expiry checks.
/// </summary>
public sealed record McpCredentials
{
    /// <summary>
    /// The MCP server URL these credentials are for.
    /// </summary>
    public required string ServerUrl { get; init; }

    /// <summary>
    /// OAuth client ID (may be dynamically registered).
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// The access token for API authentication.
    /// </summary>
    public required string AccessToken { get; init; }

    /// <summary>
    /// Refresh token for obtaining new access tokens.
    /// </summary>
    public string? RefreshToken { get; init; }

    /// <summary>
    /// When the access token expires (UTC).
    /// </summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// OAuth scopes granted (space-separated).
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    /// Whether the access token has expired.
    /// Uses 30-second buffer to avoid edge cases.
    /// </summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddSeconds(-30);

    /// <summary>
    /// Whether a refresh can be attempted (has refresh token).
    /// </summary>
    public bool CanRefresh => !string.IsNullOrEmpty(RefreshToken);
}
