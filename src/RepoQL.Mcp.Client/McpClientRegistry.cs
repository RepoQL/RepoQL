using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RepoQL.Data.DuckDB;

namespace RepoQL.Mcp.Client;

/// <summary>
/// Manages MCP client connections with async startup support.
/// Thread-safe, caches connections, excludes self-references.
/// Implements IMcpToolCaller for SQL UDF integration.
/// </summary>
public sealed class McpClientRegistry : IAsyncDisposable, IMcpToolCaller
{
    private readonly IReadOnlyDictionary<string, McpServerConfig> _configs;
    private readonly ConcurrentDictionary<string, IMcpClient> _clients = new();
    private readonly ConcurrentDictionary<string, Task<IMcpClient>> _connectingClients = new();
    private readonly ILogger _logger;
    private readonly string _selfServerName;
    private readonly McpCredentialProvider _credentialProvider;
    private bool _disposed;
    private bool _started;

    private McpClientRegistry(
        IReadOnlyDictionary<string, McpServerConfig> configs,
        string selfServerName,
        ILogger logger,
        McpCredentialProvider? credentialProvider = null)
    {
        _configs = configs;
        _selfServerName = selfServerName;
        _logger = logger;
        _credentialProvider = credentialProvider ?? new McpCredentialProvider(logger);
    }

    /// <summary>
    /// Creates a registry from a .mcp.json config file (does not connect yet).
    /// </summary>
    public static McpClientRegistry Create(
        string configPath,
        string selfServerName = "repoql",
        ILogger? logger = null)
    {
        var configs = McpConfigLoader.LoadFromFile(configPath);
        return new McpClientRegistry(configs, selfServerName, logger ?? NullLogger.Instance);
    }

    /// <summary>
    /// Creates a registry by loading configs from multiple paths (later paths override earlier).
    /// Searches: .mcp.json, .repoql.mcp.json, .repoql/.mcp.json
    /// </summary>
    public static McpClientRegistry CreateFromDirectory(
        string directory,
        string selfServerName = "repoql",
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var source = new DirectoryMcpConfigSource(directory, log);
        var configs = source.LoadConfigs()
            .Where(kvp => !string.Equals(kvp.Key, selfServerName, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        return new McpClientRegistry(configs, selfServerName, log);
    }

    /// <summary>
    /// Creates a registry by loading configs from multiple sources, including
    /// repository-level and global agent configurations.
    /// </summary>
    /// <param name="directory">Repository root directory</param>
    /// <param name="includeGlobalAgents">
    /// When true, loads MCP servers from global agent configs (Claude Code, Claude Desktop, etc.).
    /// When false, only loads repo-level configs.
    /// </param>
    /// <param name="selfServerName">Server name to exclude (prevents self-reference)</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public static McpClientRegistry CreateFromDirectoryWithGlobals(
        string directory,
        bool includeGlobalAgents = true,
        string selfServerName = "repoql",
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;

        IReadOnlyList<IMcpConfigSource> sources;
        if (includeGlobalAgents)
        {
            sources = McpConfigSourceFactory.CreateAll(directory, logger: log);
        }
        else
        {
            sources = new[] { McpConfigSourceFactory.CreateDirectorySource(directory, log) };
        }

        return CreateFromSources(sources, selfServerName, log);
    }

    /// <summary>
    /// Creates a registry from explicit config sources.
    /// Use this for full control over which sources are loaded.
    /// </summary>
    /// <param name="sources">Config sources to load from (merged by priority)</param>
    /// <param name="selfServerName">Server name to exclude (prevents self-reference)</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public static McpClientRegistry CreateFromSources(
        IEnumerable<IMcpConfigSource> sources,
        string selfServerName = "repoql",
        ILogger? logger = null)
    {
        var log = logger ?? NullLogger.Instance;
        var configs = McpConfigSourceFactory.LoadAndMerge(sources, selfServerName);
        return new McpClientRegistry(configs, selfServerName, log);
    }

    /// <summary>
    /// Creates a registry from pre-loaded configs (for testing).
    /// </summary>
    public static McpClientRegistry CreateFromConfigs(
        IReadOnlyDictionary<string, McpServerConfig> configs,
        string selfServerName = "repoql",
        ILogger? logger = null)
    {
        return new McpClientRegistry(configs, selfServerName, logger ?? NullLogger.Instance);
    }

    /// <summary>
    /// Whether the registry has started connecting to servers.
    /// </summary>
    public bool IsStarted => _started;

    /// <summary>
    /// Gets all configured servers, excluding the self-reference.
    /// </summary>
    public IReadOnlyDictionary<string, McpServerConfig> GetServers()
    {
        return _configs
            .Where(kvp => !IsSelfServer(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Checks if a server name refers to this RepoQL instance.
    /// </summary>
    public bool IsSelfServer(string serverName)
    {
        return string.Equals(serverName, _selfServerName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts connecting to all configured servers in parallel (non-blocking).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        _started = true;

        var servers = GetServers();
        if (servers.Count == 0)
        {
            _logger.LogDebug("No MCP servers configured");
            return;
        }

        _logger.LogInformation("Starting MCP client connections to {Count} servers...", servers.Count);

        // Start all connections in parallel
        var tasks = servers.Select(async kvp =>
        {
            try
            {
                await GetClientAsync(kvp.Key, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Connected to MCP server '{Server}'", kvp.Key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to MCP server '{Server}'", kvp.Key);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        _logger.LogInformation("MCP client startup complete. Connected to {Count}/{Total} servers",
            _clients.Count, servers.Count);
    }

    /// <summary>
    /// Gets or creates a connected client for the specified server.
    /// </summary>
    public async Task<IMcpClient> GetClientAsync(string serverName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsSelfServer(serverName))
            throw new InvalidOperationException($"Cannot connect to self ('{serverName}'). This would cause recursion.");

        // Fast path: already connected
        if (_clients.TryGetValue(serverName, out var existing))
            return existing;

        // Slow path: connection in progress or needs to start
        var connectTask = _connectingClients.GetOrAdd(serverName, name =>
            ConnectAsync(name, cancellationToken));

        return await connectTask.ConfigureAwait(false);
    }

    private async Task<IMcpClient> ConnectAsync(string serverName, CancellationToken cancellationToken)
    {
        if (!_configs.TryGetValue(serverName, out var config))
            throw new KeyNotFoundException($"MCP server '{serverName}' not found in configuration.");

        var client = await CreateClientAsync(config, cancellationToken).ConfigureAwait(false);
        _clients[serverName] = client;
        return client;
    }

    /// <summary>
    /// Discovers all tools from all connected servers.
    /// </summary>
    public async Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<McpToolDefinition>();

        foreach (var (serverName, _) in GetServers())
        {
            // Skip servers that failed to connect
            if (!_clients.ContainsKey(serverName))
                continue;

            try
            {
                var client = await GetClientAsync(serverName, cancellationToken).ConfigureAwait(false);
                var serverTools = await client.ListToolsAsync().ConfigureAwait(false);

                foreach (var tool in serverTools)
                {
                    JsonElement? inputSchema = null;
                    try
                    {
                        if (tool.JsonSchema is { } schema)
                        {
                            var schemaJson = JsonSerializer.Serialize(schema);
                            inputSchema = JsonSerializer.Deserialize<JsonElement>(schemaJson);
                        }
                    }
                    catch
                    {
                        // Schema conversion failed, leave as null
                    }

                    tools.Add(new McpToolDefinition
                    {
                        ServerName = serverName,
                        ToolName = tool.Name,
                        Description = tool.Description,
                        InputSchema = inputSchema
                    });
                }

                _logger.LogDebug("Discovered {ToolCount} tools from '{ServerName}'", serverTools.Count, serverName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover tools from MCP server '{ServerName}'", serverName);
            }
        }

        return tools;
    }

    /// <summary>
    /// Calls a tool synchronously (blocks). For use in DuckDB UDFs.
    /// Returns JSON string result or error JSON.
    /// </summary>
    public string CallToolSync(string serverName, string toolName, string? paramsJson)
    {
        try
        {
            return CallToolAsync(serverName, toolName, paramsJson, CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Calls a tool asynchronously.
    /// </summary>
    public async Task<string> CallToolAsync(
        string serverName,
        string toolName,
        string? paramsJson,
        CancellationToken cancellationToken = default)
    {
        if (IsSelfServer(serverName))
            throw new InvalidOperationException($"Cannot call tools on self ('{serverName}'). This would cause recursion.");

        var client = await GetClientAsync(serverName, cancellationToken).ConfigureAwait(false);

        // Parse parameters from JSON, converting JsonElement values to primitives
        Dictionary<string, object?>? parameters = null;
        if (!string.IsNullOrWhiteSpace(paramsJson))
        {
            using var doc = JsonDocument.Parse(paramsJson);
            parameters = ConvertJsonElementToDict(doc.RootElement);
        }

        var result = await client.CallToolAsync(toolName, parameters, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Extract content from result
        var contents = result.Content?.ToList() ?? [];
        if (contents.Count == 0)
            return "null";

        // If single text content, return as-is
        // The parse_structured() UDF will handle format detection and conversion in SQL
        if (contents.Count == 1 && contents[0] is TextContentBlock textContent)
        {
            return textContent.Text ?? "null";
        }

        // Multiple contents or non-text: serialize as array
        var contentArray = contents.Select(c => c switch
        {
            TextContentBlock tc => (object)tc.Text,
            _ => c.ToString() ?? ""
        }).ToArray();

        return JsonSerializer.Serialize(contentArray);
    }

    private async Task<IMcpClient> CreateClientAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation
            {
                Name = "RepoQL",
                Version = "1.0.0"
            }
        };

        if (config.IsStdio)
        {
            if (string.IsNullOrEmpty(config.Command))
                throw new InvalidOperationException($"Stdio server '{config.Name}' requires a command.");

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Name,
                Command = config.Command,
                Arguments = config.Args ?? [],
                EnvironmentVariables = config.Env
            });

            return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (config.IsHttp)
        {
            if (string.IsNullOrEmpty(config.Url))
                throw new InvalidOperationException($"HTTP server '{config.Name}' requires a URL.");

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url),
                TransportMode = HttpTransportMode.StreamableHttp
            };

            // Add headers with environment variable expansion
            var headers = config.Headers is { Count: > 0 }
                ? config.Headers.ToDictionary(h => h.Key, h => ExpandEnvironmentVariables(h.Value))
                : new Dictionary<string, string>();

            // Try to get stored credentials if not explicitly using config-only mode
            McpCredentials? storedCredentials = null;
            if (config.CredentialsFrom != McpCredentialsSource.Config)
            {
                storedCredentials = await _credentialProvider.GetCredentialsAsync(config.Url, cancellationToken).ConfigureAwait(false);

                if (storedCredentials is not null)
                {
                    // Check if expired and try to refresh
                    if (storedCredentials.IsExpired && storedCredentials.CanRefresh)
                    {
                        _logger.LogDebug("Stored credentials expired for '{Server}', attempting refresh", config.Name);
                        storedCredentials = await _credentialProvider.RefreshTokenAsync(storedCredentials, cancellationToken).ConfigureAwait(false);
                    }

                    // If we have valid credentials, use them
                    if (storedCredentials is not null && !storedCredentials.IsExpired)
                    {
                        _logger.LogInformation("Using stored credentials for '{Server}'", config.Name);
                        headers["Authorization"] = $"Bearer {storedCredentials.AccessToken}";
                    }
                    else
                    {
                        _logger.LogDebug("Stored credentials invalid or refresh failed for '{Server}', will use OAuth flow", config.Name);
                        storedCredentials = null; // Clear so we fall through to OAuth
                    }
                }
            }

            if (headers.Count > 0)
            {
                transportOptions.AdditionalHeaders = headers;
            }

            // Configure OAuth if specified and we don't have valid stored credentials
            if (config.OAuth is { } oauth && storedCredentials is null)
            {
                transportOptions.OAuth = new ClientOAuthOptions
                {
                    RedirectUri = new Uri(oauth.RedirectUri),
                    ClientId = oauth.ClientId is { } clientId ? ExpandEnvironmentVariables(clientId) : null,
                    ClientSecret = oauth.ClientSecret is { } clientSecret ? ExpandEnvironmentVariables(clientSecret) : null,
                    Scopes = oauth.Scopes,
                    AuthorizationRedirectDelegate = HandleAuthorizationUrlAsync,
                    DynamicClientRegistration = oauth.ClientId is null ? new DynamicClientRegistrationOptions
                    {
                        ClientName = oauth.ClientName ?? "RepoQL"
                    } : null
                };
            }

            var transport = new HttpClientTransport(transportOptions);
            return await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        throw new NotSupportedException($"Unsupported transport type '{config.Type}' for server '{config.Name}'.");
    }

    /// <summary>
    /// Expands ${VAR} and $VAR patterns with environment variable values.
    /// </summary>
    private static string ExpandEnvironmentVariables(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Match ${VAR} or $VAR patterns
        return System.Text.RegularExpressions.Regex.Replace(value, @"\$\{([^}]+)\}|\$([A-Za-z_][A-Za-z0-9_]*)", match =>
        {
            var varName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return Environment.GetEnvironmentVariable(varName) ?? match.Value;
        });
    }

    /// <summary>
    /// Handles OAuth authorization by starting a localhost HTTP listener and opening the browser.
    /// </summary>
    private static async Task<string?> HandleAuthorizationUrlAsync(
        Uri authorizationUrl,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("=== OAuth Authorization Required ===");
        Console.WriteLine($"Opening browser to: {authorizationUrl}");
        Console.WriteLine();

        var listenerPrefix = redirectUri.GetLeftPart(UriPartial.Authority);
        if (!listenerPrefix.EndsWith('/'))
            listenerPrefix += "/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(listenerPrefix);

        try
        {
            listener.Start();
            Console.WriteLine($"Waiting for OAuth callback on: {listenerPrefix}");

            // Open browser
            OpenBrowser(authorizationUrl);

            // Wait for callback with cancellation support
            var contextTask = listener.GetContextAsync();
            var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);

            if (completedTask != contextTask)
            {
                Console.WriteLine("OAuth authorization cancelled.");
                return null;
            }

            var context = await contextTask.ConfigureAwait(false);
            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
            var code = query["code"];
            var error = query["error"];

            // Send response to browser
            const string responseHtml = """
                <html>
                <body style="font-family: system-ui; display: flex; justify-content: center; align-items: center; height: 100vh; margin: 0;">
                    <div style="text-align: center;">
                        <h1>Authentication Complete</h1>
                        <p>You can close this window and return to RepoQL.</p>
                    </div>
                </body>
                </html>
                """;
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentLength64 = buffer.Length;
            context.Response.ContentType = "text/html";
            await context.Response.OutputStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            context.Response.Close();

            if (!string.IsNullOrEmpty(error))
            {
                Console.WriteLine($"OAuth error: {error}");
                return null;
            }

            if (string.IsNullOrEmpty(code))
            {
                Console.WriteLine("No authorization code received.");
                return null;
            }

            Console.WriteLine("Authorization successful.");
            Console.WriteLine();
            return code;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("OAuth authorization cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OAuth error: {ex.Message}");
            return null;
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();
        }
    }

    /// <summary>
    /// Opens the specified URL in the default browser.
    /// </summary>
    private static void OpenBrowser(Uri url)
    {
        // Only allow HTTP/HTTPS
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            Console.WriteLine($"Error: Only HTTP and HTTPS URLs are allowed.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url.ToString(),
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open browser: {ex.Message}");
            Console.WriteLine($"Please manually open: {url}");
        }
    }

    private static Dictionary<string, McpServerConfig> LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return new Dictionary<string, McpServerConfig>();

        var json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);

        var configs = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        // Try "mcpServers" first (Claude format), then "servers"
        JsonElement serversElement;
        if (!doc.RootElement.TryGetProperty("mcpServers", out serversElement) &&
            !doc.RootElement.TryGetProperty("servers", out serversElement))
        {
            return configs;
        }

        foreach (var serverProp in serversElement.EnumerateObject())
        {
            var serverName = serverProp.Name;
            var serverObj = serverProp.Value;

            var type = serverObj.TryGetProperty("type", out var typeProp)
                ? typeProp.GetString() ?? "stdio"
                : "stdio";

            var config = new McpServerConfig
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
                OAuth = ParseOAuthConfig(serverObj),
                CredentialsFrom = ParseCredentialsFrom(serverObj)
            };

            configs[serverName] = config;
        }

        return configs;
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

    private static McpCredentialsSource ParseCredentialsFrom(JsonElement serverObj)
    {
        if (!serverObj.TryGetProperty("credentialsFrom", out var credProp))
            return McpCredentialsSource.Auto;

        var value = credProp.GetString()?.ToLowerInvariant();
        return value switch
        {
            "claude" => McpCredentialsSource.Claude,
            "repoql" => McpCredentialsSource.Repoql,
            "config" => McpCredentialsSource.Config,
            _ => McpCredentialsSource.Auto
        };
    }

    /// <summary>
    /// Converts a JsonElement to a Dictionary with primitive values.
    /// JsonSerializer.Deserialize produces JsonElement values which MCP SDK may not handle.
    /// </summary>
    private static Dictionary<string, object?>? ConvertJsonElementToDict(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElement(prop.Value);
        }
        return dict;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => ConvertJsonElementToDict(element),
            _ => element.GetRawText()
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients.Values)
        {
            try
            {
                if (client is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else if (client is IDisposable disposable)
                    disposable.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP client");
            }
        }

        _clients.Clear();
        _connectingClients.Clear();
    }
}
