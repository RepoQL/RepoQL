using System.Text.Json;

namespace RepoQL.Mcp.Client;

/// <summary>
/// Configuration for an MCP server loaded from .mcp.json
/// </summary>
public sealed record McpServerConfig
{
    public required string Name { get; init; }
    public required string Type { get; init; } // "stdio" or "http"
    public string? Command { get; init; }      // For stdio transport
    public string[]? Args { get; init; }       // For stdio transport
    public string? Url { get; init; }          // For http transport
    public Dictionary<string, string>? Env { get; init; }     // Environment variables (stdio)
    public Dictionary<string, string>? Headers { get; init; } // HTTP headers (http) - supports ${VAR} expansion
    public McpOAuthConfig? OAuth { get; init; } // OAuth configuration (http)

    public bool IsStdio => string.Equals(Type, "stdio", StringComparison.OrdinalIgnoreCase);
    public bool IsHttp => string.Equals(Type, "http", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// OAuth configuration for HTTP-based MCP servers.
/// </summary>
public sealed record McpOAuthConfig
{
    /// <summary>
    /// The OAuth redirect URI (e.g., "http://localhost:1179/callback").
    /// Required for OAuth flow.
    /// </summary>
    public required string RedirectUri { get; init; }

    /// <summary>
    /// OAuth client ID. If not provided, dynamic client registration will be attempted.
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>
    /// OAuth client secret. Optional for public clients using PKCE.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Client name for dynamic registration. Defaults to "RepoQL".
    /// </summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// OAuth scopes to request. If not specified, uses scopes from server metadata.
    /// </summary>
    public string[]? Scopes { get; init; }
}

/// <summary>
/// Metadata about an MCP tool discovered from a server
/// </summary>
public sealed record McpToolDefinition
{
    public required string ServerName { get; init; }
    public required string ToolName { get; init; }
    public string? Description { get; init; }
    public JsonElement? InputSchema { get; init; }
}

/// <summary>
/// A parameter extracted from an MCP tool's input schema
/// </summary>
public sealed record McpToolParameter
{
    /// <summary>Sanitized name safe for SQL identifiers (lowercased, special chars replaced)</summary>
    public required string Name { get; init; }

    /// <summary>Original name from JSON schema (for JSON key in MCP calls)</summary>
    public required string OriginalName { get; init; }

    public required string Type { get; init; } // "string", "integer", "boolean", "object", "array"
    public bool Required { get; init; }
    public string? Description { get; init; }
    public JsonElement? Default { get; init; }
}
