using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Runs a stdio JSON-RPC proxy between Claude Code and a repoql mcp subprocess.
/// Complexity: Manages process lifecycle, bidirectional streaming, and response enrichment in one bounded unit.
/// </summary>
internal sealed class McpStdioProxy
{
    private const int ShutdownTimeoutMs = 5000;
    private const int UnexpectedExitCode = 1;
    private const int AspirePollIntervalSeconds = 5;

    private readonly RepoqlSubprocessOptions _options;
    private readonly ConcurrentDictionary<string, InflightRequest> _inflight = new();
    private readonly SemaphoreSlim _stdoutGate = new(1, 1);
    private readonly HarnessSessionInfo _session;
    private readonly HarnessToolRouter _toolRouter;
    private readonly AspireHostStateMonitor _hostStateMonitor;
    private readonly HarnessOperationState _operationState;
    private readonly Uri _aspireEndpoint;
    private int _shutdownRequested;
    private bool _unexpectedExit;
    private string? _lastCrashContext;
    private Process? _process;
    private Process? _orchestratorProcess;

    public McpStdioProxy(RepoqlSubprocessOptions options)
    {
        _options = options;
        _session = new HarnessSessionInfo(HarnessSessionId.Create(), DateTimeOffset.UtcNow);
        _aspireEndpoint = AspireMcpClient.ResolveEndpoint(Environment.GetEnvironmentVariable("ASPIRE_MCP_URL"));
        var aspireEndpoint = _aspireEndpoint;
        _operationState = new HarnessOperationState();
        var aspireClient = new AspireMcpClient(aspireEndpoint);
        _hostStateMonitor = new AspireHostStateMonitor(aspireClient, TimeSpan.FromSeconds(AspirePollIntervalSeconds));
        var operationCoordinator = new HarnessOperationCoordinator();
        var lifecycleOperations = new HarnessLifecycleOperations(aspireClient, _hostStateMonitor);
        _toolRouter = new HarnessToolRouter(
            _session,
            _hostStateMonitor,
            _operationState,
            operationCoordinator,
            lifecycleOperations,
            aspireClient);
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _orchestratorProcess = await OrchestratorLauncher.EnsureRunningAsync(_aspireEndpoint, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            _process = StartSubprocess();
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"[HARNESS] Failed to start repoql mcp: {e.Message}");
            return UnexpectedExitCode;
        }

        using var stdoutWriter = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true, NewLine = "\n" };
        using var stdinWriter = new StreamWriter(_process.StandardInput.BaseStream) { AutoFlush = true, NewLine = "\n" };
        var stdinReader = new LineBufferReader(Console.OpenStandardInput());
        var stdoutReader = new LineBufferReader(_process.StandardOutput.BaseStream);
        var stderrReader = new LineBufferReader(_process.StandardError.BaseStream);

        using var inputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var cancelRegistration = cancellationToken.Register(() =>
        {
            inputCts.Cancel();
            _ = RequestShutdownAsync("cancellation");
        });

        _hostStateMonitor.Start(inputCts.Token);

        ConsoleCancelEventHandler? cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            inputCts.Cancel();
            _ = RequestShutdownAsync("SIGINT");
        };
        EventHandler? exitHandler = (_, _) =>
        {
            inputCts.Cancel();
            RequestShutdownSync("SIGTERM");
        };

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += exitHandler;

        try
        {
            var stdinTask = PumpInputAsync(stdinReader, stdinWriter, stdoutWriter, inputCts.Token);
            var stdoutTask = PumpOutputAsync(stdoutReader, stdoutWriter, CancellationToken.None);
            var stderrTask = PumpStderrAsync(stderrReader, CancellationToken.None);
            var exitTask = _process.WaitForExitAsync();

            var completed = await Task.WhenAny(stdinTask, exitTask).ConfigureAwait(false);
            if (completed == stdinTask)
            {
                await RequestShutdownAsync("stdin closed");
            }
            else if (completed == exitTask && Interlocked.CompareExchange(ref _shutdownRequested, 0, 0) == 0)
            {
                _unexpectedExit = true;
                _lastCrashContext = BuildCrashContext(_process.ExitCode);
                await Console.Error.WriteLineAsync($"[HARNESS] repoql mcp exited unexpectedly with code {_process.ExitCode}");
                inputCts.Cancel();
            }

            await exitTask.ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            if (_unexpectedExit)
            {
                await FailInflightRequestsAsync(stdoutWriter, $"RepoQL MCP subprocess exited with code {_process.ExitCode}.");
                return UnexpectedExitCode;
            }

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= exitHandler;
            inputCts.Cancel();
            await _hostStateMonitor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Process StartSubprocess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FileName,
            Arguments = _options.Arguments,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("repoql mcp failed to start.");

        Console.Error.WriteLine($"[HARNESS] Spawned subprocess: {startInfo.FileName} {startInfo.Arguments}");
        return process;
    }

    private async Task PumpInputAsync(LineBufferReader reader, StreamWriter writer, TextWriter stdoutWriter, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                var harnessResponseTask = _toolRouter.TryHandleToolCallAsync(line, _process?.Id, cancellationToken);
                if (harnessResponseTask is not null)
                {
                    // Fire-and-forget is safe: JSON-RPC responses correlate by id (order doesn't matter) and _stdoutGate serializes writes.
                    _ = WriteToolResponseAsync(harnessResponseTask, stdoutWriter);
                    continue;
                }

                if (TryBlockOperationInProgressToolCall(line, out var operationBlockedResponse))
                {
                    await WriteStdoutAsync(stdoutWriter, operationBlockedResponse).ConfigureAwait(false);
                    continue;
                }

                if (TryBlockHostStoppedToolCall(line, out var blockedResponse))
                {
                    await WriteStdoutAsync(stdoutWriter, blockedResponse).ConfigureAwait(false);
                    continue;
                }

                TrackRequest(line);
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private async Task PumpOutputAsync(LineBufferReader reader, StreamWriter writer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                var rewritten = TryRewriteResponse(line, out var updated) ? updated : line;
                await WriteStdoutAsync(writer, rewritten).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private async Task PumpStderrAsync(LineBufferReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;

                await Console.Error.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private void TrackRequest(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement))
                return;

            if (!JsonRpcId.TryParse(idElement, out var id))
                return;

            string? method = null;
            if (root.TryGetProperty("method", out var methodElement) &&
                methodElement.ValueKind == JsonValueKind.String)
            {
                method = methodElement.GetString();
            }

            var kind = RequestKind.Other;
            if (string.Equals(method, "tools/call", StringComparison.Ordinal))
                kind = RequestKind.ToolCall;
            else if (string.Equals(method, "initialize", StringComparison.Ordinal))
                kind = RequestKind.Initialize;
            else if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                kind = RequestKind.ToolsList;

            var requestId = kind == RequestKind.ToolCall ? HarnessRequestId.Create() : null;
            var inflight = new InflightRequest(id, kind, requestId, Stopwatch.GetTimestamp());

            if (!_inflight.TryAdd(id.Key, inflight))
            {
                Console.Error.WriteLine($"[HARNESS] Duplicate JSON-RPC id seen: {id.RawJson}");
            }
        }
        catch (JsonException)
        {
            // Pass through without tracking if payload isn't JSON.
        }
    }

    private bool TryRewriteResponse(string line, out string updated)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var idElement))
            {
                updated = line;
                return false;
            }

            if (!JsonRpcId.TryParse(idElement, out var id))
            {
                updated = line;
                return false;
            }

            if (!_inflight.TryRemove(id.Key, out var inflight))
            {
                updated = line;
                return false;
            }

            if (inflight.Kind == RequestKind.Initialize)
            {
                if (root.TryGetProperty("error", out _))
                {
                    updated = line;
                    return false;
                }

                return HarnessToolCatalog.TryMergeInitializeResponse(line, out updated);
            }

            if (inflight.Kind == RequestKind.ToolsList)
            {
                if (root.TryGetProperty("error", out _))
                {
                    updated = line;
                    return false;
                }

                return HarnessToolCatalog.TryMergeToolsListResponse(line, out updated);
            }

            if (inflight.Kind != RequestKind.ToolCall || inflight.RequestId is null)
            {
                updated = line;
                return false;
            }

            if (root.TryGetProperty("error", out _))
            {
                updated = line;
                return false;
            }

            var durationMs = (long)Stopwatch.GetElapsedTime(inflight.StartTimestamp).TotalMilliseconds;
            return HarnessMetadataInjector.TryInjectToolResponse(line, inflight.RequestId, durationMs, out updated);
        }
        catch (JsonException)
        {
            updated = line;
            return false;
        }
    }

    private async Task RequestShutdownAsync(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            return;

        await Console.Error.WriteLineAsync($"[HARNESS] Shutting down: {reason}");
        await StopSubprocessAsync().ConfigureAwait(false);
    }

    private void RequestShutdownSync(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1)
            return;

        Console.Error.WriteLine($"[HARNESS] Shutting down: {reason}");
        StopSubprocessSync();
    }

    private async Task StopSubprocessAsync()
    {
        var process = _process;
        if (process is null || process.HasExited)
            return;

        try
        {
            process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
            // Covers ObjectDisposedException (subclass) too.
        }

        var exited = await WaitForExitAsync(process, TimeSpan.FromMilliseconds(ShutdownTimeoutMs)).ConfigureAwait(false);
        if (!exited)
        {
            process.Kill(true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private void StopSubprocessSync()
    {
        var process = _process;
        if (process is null || process.HasExited)
            return;

        try
        {
            process.StandardInput.Close();
        }
        catch (InvalidOperationException)
        {
            // Covers ObjectDisposedException (subclass) too.
        }

        if (!process.WaitForExit(ShutdownTimeoutMs))
        {
            process.Kill(true);
            process.WaitForExit(ShutdownTimeoutMs);
        }
    }

    private async Task FailInflightRequestsAsync(StreamWriter stdoutWriter, string message)
    {
        var errorMessage = _lastCrashContext is not null
            ? $"{message}\n{_lastCrashContext}"
            : message;

        foreach (var entry in _inflight.ToArray())
        {
            if (!_inflight.TryRemove(entry.Key, out var inflight))
                continue;

            var errorJson = JsonRpcErrorBuilder.BuildError(inflight.Id.RawJson, -32000, errorMessage);
            await WriteStdoutAsync(stdoutWriter, errorJson).ConfigureAwait(false);
        }
    }

    private async Task WriteToolResponseAsync(Task<string> responseTask, TextWriter stdoutWriter)
    {
        try
        {
            var response = await responseTask.ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response))
                await WriteStdoutAsync(stdoutWriter, response).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[HARNESS] Failed to handle harness tool call: {ex.Message}");
        }
    }

    private async Task WriteStdoutAsync(TextWriter writer, string line)
    {
        await _stdoutGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        finally
        {
            _stdoutGate.Release();
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var exitTask = process.WaitForExitAsync();
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == exitTask;
    }

    /// <summary>
    /// Purpose: Captures per-request tracking data for response correlation.
    /// Complexity: Stores only what the proxy needs (id, timing, tool flag) to keep memory and logic tight.
    /// </summary>
    private enum RequestKind
    {
        Other,
        ToolCall,
        Initialize,
        ToolsList
    }

    private sealed record InflightRequest(JsonRpcId Id, RequestKind Kind, string? RequestId, long StartTimestamp);

    private bool TryBlockOperationInProgressToolCall(string json, out string responseJson)
    {
        responseJson = string.Empty;
        var snapshot = _operationState.GetSnapshot();
        if (!snapshot.IsInProgress)
            return false;

        if (!TryParseToolCall(json, out var id, out var toolName))
            return false;

        if (string.Equals(toolName, HarnessToolCatalog.StatusToolName, StringComparison.Ordinal))
            return false;

        var startTimestamp = Stopwatch.GetTimestamp();
        var requestId = HarnessRequestId.Create();
        var operationName = snapshot.OperationName ?? "operation";
        var displayName = snapshot.DisplayName ?? "Operation";
        var payload = JsonSerializer.Serialize(new
        {
            error = "operation_in_progress",
            operation = operationName,
            message = $"{displayName} in progress. Please wait.",
            started_at = snapshot.StartedAt.HasValue ? HarnessTimestampFormatter.Format(snapshot.StartedAt.Value) : null
        });

        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        responseJson = HarnessToolResponseBuilder.BuildToolResponse(id, payload, requestId, durationMs, isError: true);
        return true;
    }

    private bool TryBlockHostStoppedToolCall(string json, out string responseJson)
    {
        responseJson = string.Empty;
        var snapshot = _hostStateMonitor.GetSnapshot();
        if (!snapshot.IsStopped)
            return false;

        if (!TryParseToolCall(json, out var id, out var toolName))
            return false;

        if (HarnessToolCatalog.IsHarnessToolName(toolName))
            return false;

        var startTimestamp = Stopwatch.GetTimestamp();
        var errorPayload = _lastCrashContext
            ?? JsonSerializer.Serialize(new
            {
                error = "host_stopped",
                message = "RepoQL host is not running. Use harness.restart() to start it."
            });

        var requestId = HarnessRequestId.Create();
        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        responseJson = HarnessToolResponseBuilder.BuildToolResponse(id, errorPayload, requestId, durationMs, isError: true);
        return true;
    }

    private static bool TryParseToolCall(string json, out JsonRpcId id, out string toolName)
    {
        id = default;
        toolName = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                !string.Equals(methodElement.GetString(), "tools/call", StringComparison.Ordinal))
            {
                return false;
            }

            if (!root.TryGetProperty("params", out var paramsElement) ||
                paramsElement.ValueKind != JsonValueKind.Object ||
                !paramsElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!root.TryGetProperty("id", out var idElement) || !JsonRpcId.TryParse(idElement, out id))
                return false;

            toolName = nameElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(toolName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string BuildCrashContext(int exitCode)
    {
        var inflightTools = _inflight.Values
            .Where(r => r.Kind == RequestKind.ToolCall)
            .Select(r => r.Id.RawJson)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            @event = "unexpected_exit",
            exit_code = exitCode,
            message = $"RepoQL MCP subprocess exited unexpectedly (code {exitCode}). This was NOT a harness-initiated shutdown — it's a bug or crash.",
            session_id = _session.SessionId,
            inflight_requests = _inflight.Count,
            inflight_tool_ids = inflightTools,
            actions = new[]
            {
                "harness.logs(since: '30s') - check recent logs for the cause",
                "harness.restart() - restart the host",
                "harness.build() - rebuild and restart if code changes are needed"
            }
        });
    }
}
