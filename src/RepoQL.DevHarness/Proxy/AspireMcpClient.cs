using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Connect to the Aspire MCP server over HTTP streaming and invoke tools.
/// Complexity: Manages client reuse, reconnects on failure, and normalizes resource output.
/// </summary>
internal sealed class AspireMcpClient : IAsyncDisposable, IAspireTelemetryClient
{
    private const string DefaultAspireUrl = "http://localhost:15011";
    private const string McpPath = "/mcp";

    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMcpClient? _client;
    private bool _disposed;

    public AspireMcpClient(Uri endpoint)
    {
        _endpoint = endpoint;
    }

    public static Uri ResolveEndpoint(string? configuredUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredUrl) ? DefaultAspireUrl : configuredUrl.Trim();
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        if (baseUri.AbsolutePath.EndsWith(McpPath, StringComparison.OrdinalIgnoreCase))
            return baseUri;

        return new Uri(baseUri, McpPath);
    }

    public async Task<IReadOnlyList<AspireResource>> ListResourcesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.CallToolAsync("list_resources", null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var text = ExtractText(result);
            return AspireResourceParser.Parse(text);
        }
        catch
        {
            await ResetClientAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AspireTelemetryResult> ListStructuredLogsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
        => await CallToolAsync("list_structured_logs", arguments, cancellationToken).ConfigureAwait(false);

    public async Task<AspireTelemetryResult> ListTracesAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
        => await CallToolAsync("list_traces", arguments, cancellationToken).ConfigureAwait(false);

    public async Task<AspireTelemetryResult> ListConsoleLogsAsync(string resourceName, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?> { ["resourceName"] = resourceName };
        return await CallToolAsync("list_console_logs", arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AspireTelemetryResult> ListTraceStructuredLogsAsync(string traceId, CancellationToken cancellationToken)
    {
        var arguments = new Dictionary<string, object?> { ["traceId"] = traceId };
        return await CallToolAsync("list_trace_structured_logs", arguments, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AspireCommandResult> ExecuteResourceCommandAsync(string resourceName, string commandName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
            return AspireCommandResult.Fail("Resource name is required.");
        if (string.IsNullOrWhiteSpace(commandName))
            return AspireCommandResult.Fail("Command name is required.");

        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, object?> arguments = new Dictionary<string, object?>
            {
                ["resourceName"] = resourceName,
                ["commandName"] = commandName
            };

            var result = await client.CallToolAsync("execute_resource_command", arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var text = ExtractText(result);
            if (IsError(result))
                return AspireCommandResult.Fail(text ?? "Aspire command failed.");

            return AspireCommandResult.Ok(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ResetClientAsync().ConfigureAwait(false);
            return AspireCommandResult.Fail(ex.Message);
        }
    }

    private async Task<IMcpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
                return _client;

            var options = new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "RepoQL.DevHarness",
                    Version = "1.0.0"
                }
            };

            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = _endpoint,
                TransportMode = HttpTransportMode.StreamableHttp
            };

            var transport = new HttpClientTransport(transportOptions);
            _client = await McpClient.CreateAsync(transport, options, cancellationToken: cancellationToken).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetClientAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AspireTelemetryResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var text = ExtractText(result);
            if (IsError(result))
                return AspireTelemetryResult.Fail(text ?? $"{toolName} failed.");

            return AspireTelemetryResult.Ok(text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ResetClientAsync().ConfigureAwait(false);
            return AspireTelemetryResult.Fail(ex.Message);
        }
    }

    private static string? ExtractText(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
            return null;

        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
            {
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(textBlock.Text);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool IsError(CallToolResult result)
        => result.IsError == true;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _gate.Dispose();
    }
}
