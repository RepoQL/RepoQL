using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Routes and executes harness-prefixed MCP tool calls locally.
/// Complexity: Validates JSON-RPC envelopes and emits MCP-compliant tool responses.
/// </summary>
internal sealed class HarnessToolRouter
{
    private const string ToolCallMethod = "tools/call";
    private const string DefaultResourceName = "host";
    private const int DefaultLogLimit = 100;
    private const int MaxLogLimit = 1000;
    private const int DefaultTraceLimit = 10;
    private const int MaxTraceLimit = 100;
    private const int DefaultConsoleLogLimit = 200;
    private const int MaxConsoleLogLimit = 2000;
    private static readonly TimeSpan OperationWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OperationWaitPollInterval = TimeSpan.FromSeconds(2);
    private readonly HarnessSessionInfo _session;
    private readonly IHostStateProvider _hostStateProvider;
    private readonly IHarnessOperationState _operationState;
    private readonly IHarnessOperationCoordinator _operationCoordinator;
    private readonly IHarnessLifecycleOperations _lifecycleOperations;
    private readonly IAspireTelemetryClient _telemetryClient;

    public HarnessToolRouter(
        HarnessSessionInfo session,
        IHostStateProvider hostStateProvider,
        IHarnessOperationState operationState,
        IHarnessOperationCoordinator operationCoordinator,
        IHarnessLifecycleOperations lifecycleOperations,
        IAspireTelemetryClient telemetryClient)
    {
        _session = session;
        _hostStateProvider = hostStateProvider;
        _operationState = operationState;
        _operationCoordinator = operationCoordinator;
        _lifecycleOperations = lifecycleOperations;
        _telemetryClient = telemetryClient;
    }

    public Task<string>? TryHandleToolCallAsync(string json, int? subprocessPid, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                !string.Equals(methodElement.GetString(), ToolCallMethod, StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("params", out var paramsElement) ||
                paramsElement.ValueKind != JsonValueKind.Object ||
                !paramsElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var toolName = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(toolName) || !HarnessToolCatalog.IsHarnessToolName(toolName))
            {
                return null;
            }

            string? argumentsJson = null;
            if (paramsElement.TryGetProperty("arguments", out var argumentsElement) &&
                argumentsElement.ValueKind == JsonValueKind.Object)
            {
                argumentsJson = argumentsElement.GetRawText();
            }

            if (!root.TryGetProperty("id", out var idElement) || !JsonRpcId.TryParse(idElement, out var id))
            {
                var errorJson = JsonRpcErrorBuilder.BuildError("null", -32602, "Invalid request id.");
                return Task.FromResult(errorJson);
            }

            var startTimestamp = Stopwatch.GetTimestamp();
            var requestId = HarnessRequestId.Create();

            if (string.Equals(toolName, HarnessToolCatalog.StatusToolName, StringComparison.Ordinal))
            {
                var statusPayload = BuildStatusPayload(subprocessPid);
                var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var response = HarnessToolResponseBuilder.BuildToolResponse(id, statusPayload, requestId, durationMs, isError: false);
                return Task.FromResult(response);
            }

            if (string.Equals(toolName, HarnessToolCatalog.LogsToolName, StringComparison.Ordinal))
                return HandleLogsAsync(id, requestId, startTimestamp, argumentsJson, cancellationToken);

            if (string.Equals(toolName, HarnessToolCatalog.TracesToolName, StringComparison.Ordinal))
                return HandleTracesAsync(id, requestId, startTimestamp, argumentsJson, cancellationToken);

            if (string.Equals(toolName, HarnessToolCatalog.ConsoleLogsToolName, StringComparison.Ordinal))
                return HandleConsoleLogsAsync(id, requestId, startTimestamp, argumentsJson, cancellationToken);

            if (string.Equals(toolName, HarnessToolCatalog.TraceLogsToolName, StringComparison.Ordinal))
                return HandleTraceLogsAsync(id, requestId, startTimestamp, argumentsJson, cancellationToken);

            if (string.Equals(toolName, HarnessToolCatalog.WaitForOperationToolName, StringComparison.Ordinal))
                return HandleWaitForOperationAsync(id, requestId, startTimestamp, cancellationToken);

            var operationSnapshot = _operationState.GetSnapshot();
            if (operationSnapshot.IsInProgress)
            {
                var response = BuildOperationInProgressResponse(id, requestId, startTimestamp, operationSnapshot);
                return Task.FromResult(response);
            }

            if (string.Equals(toolName, HarnessToolCatalog.BuildToolName, StringComparison.Ordinal))
                return HandleLifecycleOperationAsync(id, requestId, startTimestamp, HarnessOperationKind.Building, _lifecycleOperations.BuildAsync, cancellationToken);

            if (string.Equals(toolName, HarnessToolCatalog.DeployToolName, StringComparison.Ordinal))
                return HandleLifecycleOperationAsync(id, requestId, startTimestamp, HarnessOperationKind.Deploying, _lifecycleOperations.DeployAsync, cancellationToken);

            if (string.Equals(toolName, HarnessToolCatalog.RestartToolName, StringComparison.Ordinal))
                return HandleLifecycleOperationAsync(id, requestId, startTimestamp, HarnessOperationKind.Restarting, _lifecycleOperations.RestartAsync, cancellationToken);

            var unknown = JsonRpcErrorBuilder.BuildError(id.RawJson, -32602, $"Unknown tool: {toolName}.");
            return Task.FromResult(unknown);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string BuildStatusPayload(int? subprocessPid)
    {
        var snapshot = _hostStateProvider.GetSnapshot();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("harness");
            writer.WriteStartObject();
            writer.WriteString("session_id", _session.SessionId);
            writer.WriteString("started_at", HarnessTimestampFormatter.Format(_session.StartedAt));
            if (subprocessPid.HasValue)
                writer.WriteNumber("subprocess_pid", subprocessPid.Value);
            else
                writer.WriteNull("subprocess_pid");
            writer.WriteBoolean("aspire_connected", snapshot.AspireConnected);
            writer.WriteEndObject();

            writer.WritePropertyName("host");
            writer.WriteStartObject();
            writer.WriteString("state", snapshot.State);
            if (!string.IsNullOrWhiteSpace(snapshot.ResourceName))
                writer.WriteString("resource_name", snapshot.ResourceName);
            else
                writer.WriteNull("resource_name");
            writer.WriteEndObject();

            writer.WritePropertyName("current_operation");
            var operationSnapshot = _operationState.GetSnapshot();
            if (!operationSnapshot.IsInProgress)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("operation", operationSnapshot.OperationName);
                if (operationSnapshot.StartedAt.HasValue)
                    writer.WriteString("started_at", HarnessTimestampFormatter.Format(operationSnapshot.StartedAt.Value));
                else
                    writer.WriteNull("started_at");
                writer.WriteEndObject();
            }

            var lockDescription = _operationCoordinator.GetActiveLockDescription();
            if (lockDescription is not null)
                writer.WriteString("coordination_lock", lockDescription);
            else
                writer.WriteNull("coordination_lock");

            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private async Task<string> HandleLifecycleOperationAsync(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        HarnessOperationKind operationKind,
        Func<CancellationToken, Task<HarnessLifecycleResult>> executor,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        if (!_operationState.TryBegin(operationKind, startedAt))
        {
            return BuildOperationInProgressResponse(id, requestId, startTimestamp, _operationState.GetSnapshot());
        }

        IDisposable? lockHandle = null;
        try
        {
            var operationName = ResolveOperationName(operationKind);
            var (acquiredHandle, lockError) = _operationCoordinator.TryAcquire(_session.SessionId, operationName, startedAt);
            if (lockError is not null)
            {
                var errorDurationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var errorPayload = JsonSerializer.Serialize(new
                {
                    error = "operation_in_progress",
                    message = lockError,
                    options = new[] { "harness.wait_for_operation()", "wait and retry" }
                });
                return HarnessToolResponseBuilder.BuildToolResponse(id, errorPayload, requestId, errorDurationMs, isError: true);
            }

            lockHandle = acquiredHandle;
            var result = await executor(cancellationToken).ConfigureAwait(false);
            var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var payload = BuildOperationPayload(result, durationMs);
            LogOperationOutcome(operationKind, result);
            return HarnessToolResponseBuilder.BuildToolResponse(id, payload, requestId, durationMs, isError: !result.Success);
        }
        catch (OperationCanceledException)
        {
            var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var payload = JsonSerializer.Serialize(new
            {
                success = false,
                duration_ms = durationMs,
                error = "Operation canceled."
            });
            LogOperationCancellation(operationKind);
            return HarnessToolResponseBuilder.BuildToolResponse(id, payload, requestId, durationMs, isError: true);
        }
        finally
        {
            lockHandle?.Dispose();
            _operationState.Complete(operationKind);
        }
    }


    private async Task<string> HandleLogsAsync(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        if (!TryParseLogsArguments(argumentsJson, DateTimeOffset.UtcNow, out var query, out var error))
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "invalid_arguments", error);

        var arguments = BuildLogArguments(query);
        var result = await _telemetryClient.ListStructuredLogsAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var message = result.Error ?? "Aspire logs query failed.";
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_unavailable", message);
        }

        if (!HarnessTelemetryFormatter.TryFormatLogs(result.Content, query.Limit, out var payloadJson, out var formatError))
        {
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_response_invalid", formatError ?? "Aspire logs response was invalid.");
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return HarnessToolResponseBuilder.BuildToolResponse(id, payloadJson, requestId, durationMs, isError: false);
    }

    private async Task<string> HandleTracesAsync(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        if (!TryParseTracesArguments(argumentsJson, DateTimeOffset.UtcNow, out var query, out var error))
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "invalid_arguments", error);

        var arguments = BuildTraceArguments(query);
        var result = await _telemetryClient.ListTracesAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var message = result.Error ?? "Aspire traces query failed.";
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_unavailable", message);
        }

        if (!HarnessTelemetryFormatter.TryFormatTraces(result.Content, query.Limit, out var payloadJson, out var formatError))
        {
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_response_invalid", formatError ?? "Aspire traces response was invalid.");
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return HarnessToolResponseBuilder.BuildToolResponse(id, payloadJson, requestId, durationMs, isError: false);
    }

    private async Task<string> HandleConsoleLogsAsync(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        var resource = DefaultResourceName;
        var limit = DefaultConsoleLogLimit;
        string? contains = null;

        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                var root = document.RootElement;

                if (root.TryGetProperty("resource", out var resourceElement) &&
                    resourceElement.ValueKind == JsonValueKind.String)
                {
                    var r = resourceElement.GetString();
                    if (!string.IsNullOrWhiteSpace(r))
                        resource = r;
                }

                if (root.TryGetProperty("contains", out var containsElement) &&
                    containsElement.ValueKind == JsonValueKind.String)
                {
                    contains = containsElement.GetString();
                }

                if (root.TryGetProperty("limit", out var limitElement) &&
                    limitElement.ValueKind == JsonValueKind.Number &&
                    limitElement.TryGetInt32(out var parsedLimit) &&
                    parsedLimit > 0)
                {
                    limit = Math.Min(parsedLimit, MaxConsoleLogLimit);
                }
            }
            catch (JsonException)
            {
                // Use defaults.
            }
        }

        var result = await _telemetryClient.ListConsoleLogsAsync(resource, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var message = result.Error ?? "Aspire console logs query failed.";
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_unavailable", message);
        }

        if (!HarnessTelemetryFormatter.TryFormatConsoleLogs(result.Content, limit, contains, out var payloadJson, out var formatError))
        {
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_response_invalid", formatError ?? "Aspire console logs response was invalid.");
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return HarnessToolResponseBuilder.BuildToolResponse(id, payloadJson, requestId, durationMs, isError: false);
    }

    private async Task<string> HandleTraceLogsAsync(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        string? argumentsJson,
        CancellationToken cancellationToken)
    {
        string? traceId = null;

        if (!string.IsNullOrWhiteSpace(argumentsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                var root = document.RootElement;

                if (root.TryGetProperty("trace_id", out var traceIdElement) &&
                    traceIdElement.ValueKind == JsonValueKind.String)
                {
                    traceId = traceIdElement.GetString();
                }
            }
            catch (JsonException)
            {
                // Fall through to validation.
            }
        }

        if (string.IsNullOrWhiteSpace(traceId))
        {
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "invalid_arguments",
                "trace_id is required. Get trace IDs from harness.traces output.");
        }

        var result = await _telemetryClient.ListTraceStructuredLogsAsync(traceId, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            var message = result.Error ?? "Aspire trace logs query failed.";
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_unavailable", message);
        }

        if (!HarnessTelemetryFormatter.TryFormatLogs(result.Content, DefaultLogLimit, out var payloadJson, out var formatError))
        {
            return BuildTelemetryErrorResponse(id, requestId, startTimestamp, "aspire_response_invalid", formatError ?? "Aspire trace logs response was invalid.");
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return HarnessToolResponseBuilder.BuildToolResponse(id, payloadJson, requestId, durationMs, isError: false);
    }

    private async Task<string> HandleWaitForOperationAsync(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        var released = await _operationCoordinator
            .WaitForReleaseAsync(OperationWaitTimeout, OperationWaitPollInterval, cancellationToken)
            .ConfigureAwait(false);

        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (!released)
        {
            var errorPayload = JsonSerializer.Serialize(new
            {
                error = "operation_wait_timeout",
                message = "Timed out waiting for operation to complete."
            });

            return HarnessToolResponseBuilder.BuildToolResponse(id, errorPayload, requestId, durationMs, isError: true);
        }

        var payload = JsonSerializer.Serialize(new
        {
            message = "Operation completed."
        });

        return HarnessToolResponseBuilder.BuildToolResponse(id, payload, requestId, durationMs, isError: false);
    }

    private static string BuildOperationPayload(HarnessLifecycleResult result, long durationMs)
    {
        if (result.Success)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                duration_ms = durationMs,
                message = result.Message ?? "Operation completed successfully."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["duration_ms"] = durationMs,
            ["error"] = result.Error ?? "Operation failed."
        };

        if (!string.IsNullOrWhiteSpace(result.Output))
            payload["output"] = result.Output;

        return JsonSerializer.Serialize(payload);
    }

    private static string BuildTelemetryErrorResponse(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        string error,
        string message)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var payload = JsonSerializer.Serialize(new
        {
            error,
            message
        });

        return HarnessToolResponseBuilder.BuildToolResponse(id, payload, requestId, durationMs, isError: true);
    }

    private static IReadOnlyDictionary<string, object?> BuildLogArguments(HarnessLogsQuery query)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["resourceName"] = query.Resource
        };

        if (query.Since.HasValue)
            arguments["since"] = HarnessTimestampFormatter.Format(query.Since.Value);

        if (!string.IsNullOrWhiteSpace(query.Level))
            arguments["level"] = query.Level;

        if (!string.IsNullOrWhiteSpace(query.Contains))
            arguments["contains"] = query.Contains;

        return arguments;
    }

    private static IReadOnlyDictionary<string, object?> BuildTraceArguments(HarnessTracesQuery query)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["resourceName"] = query.Resource
        };

        if (query.Since.HasValue)
            arguments["since"] = HarnessTimestampFormatter.Format(query.Since.Value);

        if (query.HasError.HasValue)
            arguments["hasError"] = query.HasError.Value;

        return arguments;
    }

    private static bool TryParseLogsArguments(
        string? argumentsJson,
        DateTimeOffset now,
        out HarnessLogsQuery query,
        out string error)
    {
        query = new HarnessLogsQuery(null, null, null, DefaultResourceName, DefaultLogLimit);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(argumentsJson))
            return true;

        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return true;

        if (root.TryGetProperty("since", out var sinceElement) && sinceElement.ValueKind != JsonValueKind.Null)
        {
            if (sinceElement.ValueKind != JsonValueKind.String)
            {
                error = "since must be a duration string or ISO timestamp.";
                return false;
            }

            var sinceValue = sinceElement.GetString();
            if (!HarnessTimeParser.TryParse(sinceValue, now, out var since))
            {
                error = "since must be a duration string or ISO timestamp.";
                return false;
            }

            query = query with { Since = since };
        }

        if (root.TryGetProperty("level", out var levelElement) && levelElement.ValueKind != JsonValueKind.Null)
        {
            if (levelElement.ValueKind != JsonValueKind.String)
            {
                error = "level must be a string severity.";
                return false;
            }

            var levelValue = levelElement.GetString();
            if (!HarnessLogLevel.TryNormalize(levelValue, out var normalized))
            {
                error = "level must be one of: debug, info, warning, error.";
                return false;
            }

            query = query with { Level = normalized };
        }

        if (root.TryGetProperty("contains", out var containsElement) && containsElement.ValueKind != JsonValueKind.Null)
        {
            if (containsElement.ValueKind != JsonValueKind.String)
            {
                error = "contains must be a string.";
                return false;
            }

            query = query with { Contains = containsElement.GetString() };
        }

        if (root.TryGetProperty("resource", out var resourceElement) && resourceElement.ValueKind != JsonValueKind.Null)
        {
            if (resourceElement.ValueKind != JsonValueKind.String)
            {
                error = "resource must be a string.";
                return false;
            }

            var resource = resourceElement.GetString();
            if (!string.IsNullOrWhiteSpace(resource))
                query = query with { Resource = resource };
        }

        if (root.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind != JsonValueKind.Null)
        {
            if (limitElement.ValueKind != JsonValueKind.Number || !limitElement.TryGetInt32(out var limit))
            {
                error = "limit must be a positive integer.";
                return false;
            }

            if (limit <= 0)
            {
                error = "limit must be a positive integer.";
                return false;
            }

            query = query with { Limit = Math.Min(limit, MaxLogLimit) };
        }

        return true;
    }

    private static bool TryParseTracesArguments(
        string? argumentsJson,
        DateTimeOffset now,
        out HarnessTracesQuery query,
        out string error)
    {
        query = new HarnessTracesQuery(null, null, DefaultResourceName, DefaultTraceLimit);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(argumentsJson))
            return true;

        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return true;

        if (root.TryGetProperty("since", out var sinceElement) && sinceElement.ValueKind != JsonValueKind.Null)
        {
            if (sinceElement.ValueKind != JsonValueKind.String)
            {
                error = "since must be a duration string or ISO timestamp.";
                return false;
            }

            var sinceValue = sinceElement.GetString();
            if (!HarnessTimeParser.TryParse(sinceValue, now, out var since))
            {
                error = "since must be a duration string or ISO timestamp.";
                return false;
            }

            query = query with { Since = since };
        }

        if (root.TryGetProperty("has_error", out var hasErrorElement) && hasErrorElement.ValueKind != JsonValueKind.Null)
        {
            if (hasErrorElement.ValueKind == JsonValueKind.True || hasErrorElement.ValueKind == JsonValueKind.False)
            {
                query = query with { HasError = hasErrorElement.GetBoolean() };
            }
            else
            {
                error = "has_error must be a boolean.";
                return false;
            }
        }

        if (root.TryGetProperty("resource", out var resourceElement) && resourceElement.ValueKind != JsonValueKind.Null)
        {
            if (resourceElement.ValueKind != JsonValueKind.String)
            {
                error = "resource must be a string.";
                return false;
            }

            var resource = resourceElement.GetString();
            if (!string.IsNullOrWhiteSpace(resource))
                query = query with { Resource = resource };
        }

        if (root.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind != JsonValueKind.Null)
        {
            if (limitElement.ValueKind != JsonValueKind.Number || !limitElement.TryGetInt32(out var limit))
            {
                error = "limit must be a positive integer.";
                return false;
            }

            if (limit <= 0)
            {
                error = "limit must be a positive integer.";
                return false;
            }

            query = query with { Limit = Math.Min(limit, MaxTraceLimit) };
        }

        return true;
    }

    private static string BuildOperationInProgressResponse(
        JsonRpcId id,
        string requestId,
        long startTimestamp,
        HarnessOperationSnapshot snapshot)
    {
        var durationMs = (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var operationName = snapshot.OperationName ?? "operation";
        var displayName = snapshot.DisplayName ?? "Operation";
        var payload = JsonSerializer.Serialize(new
        {
            error = "operation_in_progress",
            operation = operationName,
            message = $"{displayName} in progress. Please wait.",
            started_at = snapshot.StartedAt.HasValue ? HarnessTimestampFormatter.Format(snapshot.StartedAt.Value) : null
        });

        return HarnessToolResponseBuilder.BuildToolResponse(id, payload, requestId, durationMs, isError: true);
    }

    private static string ResolveOperationName(HarnessOperationKind kind)
        => kind switch
        {
            HarnessOperationKind.Building => "building",
            HarnessOperationKind.Deploying => "deploying",
            HarnessOperationKind.Restarting => "restarting",
            _ => "operation"
        };

    private void LogOperationOutcome(HarnessOperationKind kind, HarnessLifecycleResult result)
    {
        var operation = ResolveOperationName(kind);
        if (result.Success)
        {
            Console.Error.WriteLine($"[HARNESS] Session {_session.SessionId} completed {operation}.");
        }
        else
        {
            var error = result.Error ?? "Operation failed.";
            Console.Error.WriteLine($"[HARNESS] Session {_session.SessionId} failed {operation}: {error}");
        }
    }

    private void LogOperationCancellation(HarnessOperationKind kind)
    {
        var operation = ResolveOperationName(kind);
        Console.Error.WriteLine($"[HARNESS] Session {_session.SessionId} canceled {operation}.");
    }

    private sealed record HarnessLogsQuery(
        DateTimeOffset? Since,
        string? Level,
        string? Contains,
        string Resource,
        int Limit);

    private sealed record HarnessTracesQuery(
        DateTimeOffset? Since,
        bool? HasError,
        string Resource,
        int Limit);
}
