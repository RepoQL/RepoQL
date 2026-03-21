using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RepoQL.Mcp.Client;

/// <summary>
/// Manages OAuth credentials for MCP servers from multiple sources.
///
/// Purpose: Provides unified credential lookup across Claude Code's store and RepoQL's own store,
/// with automatic token refresh support. Enables seamless auth sharing between Claude and RepoQL.
///
/// Complexity: Handles JSON parsing of two different credential formats, OAuth metadata discovery,
/// and token refresh flow. Protected by file locks for concurrent access safety.
/// </summary>
public sealed class McpCredentialProvider
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _claudeCredentialsPath;
    private readonly string _repoqlCredentialsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Creates a credential provider using default paths.
    /// </summary>
    public McpCredentialProvider(ILogger? logger = null, HttpClient? httpClient = null)
        : this(GetDefaultClaudePath(), GetDefaultRepoqlPath(), logger, httpClient)
    {
    }

    /// <summary>
    /// Creates a credential provider with custom paths (for testing).
    /// </summary>
    public McpCredentialProvider(
        string claudeCredentialsPath,
        string repoqlCredentialsPath,
        ILogger? logger = null,
        HttpClient? httpClient = null)
    {
        _claudeCredentialsPath = claudeCredentialsPath;
        _repoqlCredentialsPath = repoqlCredentialsPath;
        _logger = logger ?? NullLogger.Instance;
        _httpClient = httpClient ?? new HttpClient();
    }

    private static string GetDefaultClaudePath()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".claude", ".credentials.json");
    }

    private static string GetDefaultRepoqlPath()
    {
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userHome, ".repoql", ".mcp-credentials.json");
    }

    /// <summary>
    /// Gets credentials for an MCP server URL, checking Claude's store first, then RepoQL's.
    /// Returns null if no credentials found.
    /// </summary>
    public async Task<McpCredentials?> GetCredentialsAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        // Normalize URL for matching
        var normalizedUrl = NormalizeUrl(serverUrl);

        // Try Claude's credential store first
        var claudeCredentials = await TryGetClaudeCredentialsAsync(normalizedUrl, cancellationToken).ConfigureAwait(false);
        if (claudeCredentials is not null)
        {
            _logger.LogDebug("Found credentials for {ServerUrl} in Claude's store", serverUrl);
            return claudeCredentials;
        }

        // Try RepoQL's credential store
        var repoqlCredentials = await TryGetRepoqlCredentialsAsync(normalizedUrl, cancellationToken).ConfigureAwait(false);
        if (repoqlCredentials is not null)
        {
            _logger.LogDebug("Found credentials for {ServerUrl} in RepoQL's store", serverUrl);
            return repoqlCredentials;
        }

        _logger.LogDebug("No credentials found for {ServerUrl}", serverUrl);
        return null;
    }

    /// <summary>
    /// Attempts to refresh an expired token using the OAuth token endpoint.
    /// Returns new credentials on success, null on failure.
    /// </summary>
    public async Task<McpCredentials?> RefreshTokenAsync(McpCredentials credentials, CancellationToken cancellationToken = default)
    {
        if (!credentials.CanRefresh)
        {
            _logger.LogDebug("Cannot refresh credentials for {ServerUrl} - no refresh token", credentials.ServerUrl);
            return null;
        }

        try
        {
            // Discover OAuth metadata
            var metadata = await DiscoverOAuthMetadataAsync(credentials.ServerUrl, cancellationToken).ConfigureAwait(false);
            if (metadata?.TokenEndpoint is null)
            {
                _logger.LogWarning("Cannot refresh - no token endpoint discovered for {ServerUrl}", credentials.ServerUrl);
                return null;
            }

            // Build refresh request
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.RefreshToken!,
                ["client_id"] = credentials.ClientId ?? ""
            });

            var response = await _httpClient.PostAsync(metadata.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Token refresh failed for {ServerUrl}: {Status}", credentials.ServerUrl, response.StatusCode);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            var newAccessToken = root.GetProperty("access_token").GetString();
            if (string.IsNullOrEmpty(newAccessToken))
            {
                _logger.LogWarning("Token refresh returned empty access token for {ServerUrl}", credentials.ServerUrl);
                return null;
            }

            // Build new credentials (refresh token may be rotated)
            var newRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : credentials.RefreshToken;
            var expiresIn = root.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
            var scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : credentials.Scope;

            var newCredentials = new McpCredentials
            {
                ServerUrl = credentials.ServerUrl,
                ClientId = credentials.ClientId,
                AccessToken = newAccessToken,
                RefreshToken = newRefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                Scope = scope
            };

            _logger.LogInformation("Successfully refreshed token for {ServerUrl}", credentials.ServerUrl);

            // Save to RepoQL's store
            await SaveCredentialsAsync(newCredentials, cancellationToken).ConfigureAwait(false);

            return newCredentials;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token refresh failed for {ServerUrl}", credentials.ServerUrl);
            return null;
        }
    }

    /// <summary>
    /// Saves credentials to RepoQL's credential store.
    /// </summary>
    public async Task SaveCredentialsAsync(McpCredentials credentials, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(_repoqlCredentialsPath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Read existing credentials
            var allCredentials = new Dictionary<string, McpCredentials>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_repoqlCredentialsPath))
            {
                var existingJson = await File.ReadAllTextAsync(_repoqlCredentialsPath, cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.TryGetProperty("credentials", out var credArray))
                {
                    foreach (var item in credArray.EnumerateArray())
                    {
                        var url = item.GetProperty("serverUrl").GetString();
                        if (!string.IsNullOrEmpty(url))
                        {
                            allCredentials[NormalizeUrl(url)] = ParseRepoqlCredential(item);
                        }
                    }
                }
            }

            // Update with new credentials
            allCredentials[NormalizeUrl(credentials.ServerUrl)] = credentials;

            // Write back
            var output = new
            {
                credentials = allCredentials.Values.Select(c => new
                {
                    serverUrl = c.ServerUrl,
                    clientId = c.ClientId,
                    accessToken = c.AccessToken,
                    refreshToken = c.RefreshToken,
                    expiresAt = c.ExpiresAt.ToUnixTimeMilliseconds(),
                    scope = c.Scope
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(output, JsonOptions);
            await File.WriteAllTextAsync(_repoqlCredentialsPath, json, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Saved credentials for {ServerUrl} to RepoQL store", credentials.ServerUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save credentials for {ServerUrl}", credentials.ServerUrl);
        }
    }

    private async Task<McpCredentials?> TryGetClaudeCredentialsAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        if (!File.Exists(_claudeCredentialsPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_claudeCredentialsPath, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            // Claude stores MCP credentials under "mcpOAuth" key
            if (!doc.RootElement.TryGetProperty("mcpOAuth", out var mcpOAuth))
                return null;

            // Keys are in format "servername|hash"
            foreach (var prop in mcpOAuth.EnumerateObject())
            {
                if (!prop.Value.TryGetProperty("serverUrl", out var serverUrlProp))
                    continue;

                var storedUrl = serverUrlProp.GetString();
                if (string.IsNullOrEmpty(storedUrl))
                    continue;

                if (NormalizeUrl(storedUrl) == normalizedUrl)
                {
                    return ParseClaudeCredential(prop.Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading Claude credentials");
        }

        return null;
    }

    private async Task<McpCredentials?> TryGetRepoqlCredentialsAsync(string normalizedUrl, CancellationToken cancellationToken)
    {
        if (!File.Exists(_repoqlCredentialsPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_repoqlCredentialsPath, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("credentials", out var credArray))
                return null;

            foreach (var item in credArray.EnumerateArray())
            {
                var storedUrl = item.GetProperty("serverUrl").GetString();
                if (string.IsNullOrEmpty(storedUrl))
                    continue;

                if (NormalizeUrl(storedUrl) == normalizedUrl)
                {
                    return ParseRepoqlCredential(item);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error reading RepoQL credentials");
        }

        return null;
    }

    private static McpCredentials ParseClaudeCredential(JsonElement element)
    {
        var expiresAt = element.TryGetProperty("expiresAt", out var ea)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ea.GetInt64())
            : DateTimeOffset.MaxValue;

        return new McpCredentials
        {
            ServerUrl = element.GetProperty("serverUrl").GetString() ?? "",
            ClientId = element.TryGetProperty("clientId", out var ci) ? ci.GetString() : null,
            AccessToken = element.GetProperty("accessToken").GetString() ?? "",
            RefreshToken = element.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null,
            ExpiresAt = expiresAt,
            Scope = element.TryGetProperty("scope", out var sc) ? sc.GetString() : null
        };
    }

    private static McpCredentials ParseRepoqlCredential(JsonElement element)
    {
        var expiresAt = element.TryGetProperty("expiresAt", out var ea)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ea.GetInt64())
            : DateTimeOffset.MaxValue;

        return new McpCredentials
        {
            ServerUrl = element.GetProperty("serverUrl").GetString() ?? "",
            ClientId = element.TryGetProperty("clientId", out var ci) ? ci.GetString() : null,
            AccessToken = element.GetProperty("accessToken").GetString() ?? "",
            RefreshToken = element.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null,
            ExpiresAt = expiresAt,
            Scope = element.TryGetProperty("scope", out var sc) ? sc.GetString() : null
        };
    }

    private async Task<OAuthMetadata?> DiscoverOAuthMetadataAsync(string serverUrl, CancellationToken cancellationToken)
    {
        try
        {
            // Try .well-known/oauth-authorization-server first, then .well-known/openid-configuration
            var baseUri = new Uri(serverUrl);
            var wellKnownUrls = new[]
            {
                new Uri(baseUri, "/.well-known/oauth-authorization-server"),
                new Uri(baseUri, "/.well-known/openid-configuration")
            };

            foreach (var wellKnownUrl in wellKnownUrls)
            {
                try
                {
                    var response = await _httpClient.GetAsync(wellKnownUrl, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var tokenEndpoint = root.TryGetProperty("token_endpoint", out var te) ? te.GetString() : null;
                    if (!string.IsNullOrEmpty(tokenEndpoint))
                    {
                        return new OAuthMetadata { TokenEndpoint = tokenEndpoint };
                    }
                }
                catch
                {
                    // Try next URL
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OAuth metadata discovery failed for {ServerUrl}", serverUrl);
        }

        return null;
    }

    private static string NormalizeUrl(string url)
    {
        // Normalize URL for comparison: lowercase, remove trailing slash
        if (string.IsNullOrEmpty(url))
            return "";

        url = url.ToLowerInvariant().TrimEnd('/');
        return url;
    }

    private sealed class OAuthMetadata
    {
        public string? TokenEndpoint { get; init; }
    }
}
