using System.Text.Json;

namespace RepoQL.Mcp.Client.Configuration;

/// <summary>
/// Shared logic for loading MCP server configurations from JSON files.
///
/// Purpose: Centralizes MCP config file parsing, supporting both "mcpServers"
/// (Claude format) and "servers" (generic format) conventions.
///
/// Complexity: Handles JSON parsing, optional OAuth config, environment
/// variables, and headers. All sources delegate to this for consistent parsing.
/// </summary>
public static class McpConfigLoader
{
    /// <summary>
    /// Loads MCP server configurations from a JSON file.
    /// </summary>
    /// <param name="configPath">Path to the .mcp.json or similar config file</param>
    /// <returns>Dictionary of server name to configuration</returns>
    public static Dictionary<string, McpServerConfig> LoadFromFile(string configPath)
    {
        if (!File.Exists(configPath))
            return new Dictionary<string, McpServerConfig>();

        var json = File.ReadAllText(configPath);
        return LoadFromJson(json);
    }

    /// <summary>
    /// Loads MCP server configurations from a JSON string.
    /// </summary>
    /// <param name="json">JSON content to parse</param>
    /// <returns>Dictionary of server name to configuration</returns>
    public static Dictionary<string, McpServerConfig> LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, McpServerConfig>();

        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        return LoadFromJsonElement(doc.RootElement);
    }

    /// <summary>
    /// Loads MCP server configurations from a JsonElement.
    /// </summary>
    public static Dictionary<string, McpServerConfig> LoadFromJsonElement(JsonElement root)
    {
        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // Try "mcpServers" first (Claude format), then "servers"
        JsonElement serversElement;
        if (!root.TryGetProperty("mcpServers", out serversElement) &&
            !root.TryGetProperty("servers", out serversElement))
        {
            return configs;
        }

        foreach (var serverProp in serversElement.EnumerateObject())
        {
            var serverName = serverProp.Name;
            var serverObj = serverProp.Value;

            var config = ParseServerConfig(serverName, serverObj);
            if (config != null)
            {
                configs[serverName] = config;
            }
        }

        return configs;
    }

    private static McpServerConfig? ParseServerConfig(string serverName, JsonElement serverObj)
    {
        var type = serverObj.TryGetProperty("type", out var typeProp)
            ? typeProp.GetString() ?? "stdio"
            : "stdio";

        return new McpServerConfig
        {
            Name = serverName,
            Type = type,
            Command = serverObj.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() : null,
            Args = serverObj.TryGetProperty("args", out var argsProp)
                ? argsProp.EnumerateArray().Select(a => a.GetString() ?? "").ToArray()
                : null,
            Url = serverObj.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null,
            Env = serverObj.TryGetProperty("env", out var envProp)
                ? envProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                : null,
            Headers = serverObj.TryGetProperty("headers", out var headersProp)
                ? headersProp.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                : null,
            OAuth = ParseOAuthConfig(serverObj)
        };
    }

    private static McpOAuthConfig? ParseOAuthConfig(JsonElement serverObj)
    {
        if (!serverObj.TryGetProperty("oauth", out var oauthObj))
            return null;

        if (!oauthObj.TryGetProperty("redirectUri", out var redirectUriProp))
            return null; // redirectUri is required

        var redirectUri = redirectUriProp.GetString();
        if (string.IsNullOrEmpty(redirectUri))
            return null;

        return new McpOAuthConfig
        {
            RedirectUri = redirectUri,
            ClientId = oauthObj.TryGetProperty("clientId", out var clientIdProp) ? clientIdProp.GetString() : null,
            ClientSecret = oauthObj.TryGetProperty("clientSecret", out var clientSecretProp) ? clientSecretProp.GetString() : null,
            ClientName = oauthObj.TryGetProperty("clientName", out var clientNameProp) ? clientNameProp.GetString() : null,
            Scopes = oauthObj.TryGetProperty("scopes", out var scopesProp)
                ? scopesProp.EnumerateArray().Select(s => s.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray()
                : null
        };
    }
}
