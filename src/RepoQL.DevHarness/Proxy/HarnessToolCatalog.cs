using System.Text;
using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Defines harness tool metadata and merges it into MCP initialize responses.
/// Complexity: Rewrites JSON-RPC envelopes while preserving all non-tool fields.
/// </summary>
internal static class HarnessToolCatalog
{
    internal const string StatusToolName = "harness.status";
    internal const string BuildToolName = "harness.build";
    internal const string RestartToolName = "harness.restart";
    internal const string DeployToolName = "harness.deploy";
    internal const string WaitForOperationToolName = "harness.wait_for_operation";
    internal const string LogsToolName = "harness.logs";
    internal const string TracesToolName = "harness.traces";
    internal const string ConsoleLogsToolName = "harness.console_logs";
    internal const string TraceLogsToolName = "harness.trace_logs";

    public static bool IsHarnessToolName(string name)
        => name.StartsWith("harness.", StringComparison.Ordinal);

    public static bool TryMergeInitializeResponse(string json, out string updatedJson)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                updatedJson = json;
                return false;
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals("result"))
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("result");
                    writer.WriteStartObject();

                    var wroteTools = false;
                    HashSet<string>? existingNames = null;

                    foreach (var resultProperty in result.EnumerateObject())
                    {
                        if (resultProperty.NameEquals("tools") && resultProperty.Value.ValueKind == JsonValueKind.Array)
                        {
                            wroteTools = true;
                            existingNames = new HashSet<string>(StringComparer.Ordinal);

                            writer.WritePropertyName("tools");
                            writer.WriteStartArray();
                            foreach (var tool in resultProperty.Value.EnumerateArray())
                            {
                                if (tool.ValueKind == JsonValueKind.Object &&
                                    tool.TryGetProperty("name", out var nameElement) &&
                                    nameElement.ValueKind == JsonValueKind.String)
                                {
                                    var name = nameElement.GetString();
                                    if (!string.IsNullOrEmpty(name))
                                        existingNames.Add(name);
                                }

                                tool.WriteTo(writer);
                            }

                            WriteTools(writer, existingNames);
                            writer.WriteEndArray();
                        }
                        else
                        {
                            resultProperty.WriteTo(writer);
                        }
                    }

                    if (!wroteTools)
                    {
                        writer.WritePropertyName("tools");
                        writer.WriteStartArray();
                        WriteTools(writer, null);
                        writer.WriteEndArray();
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
                writer.Flush();
            }

            updatedJson = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            updatedJson = json;
            return false;
        }
    }

    public static bool TryMergeToolsListResponse(string json, out string updatedJson)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                updatedJson = json;
                return false;
            }

            if (!result.TryGetProperty("tools", out var toolsElement) || toolsElement.ValueKind != JsonValueKind.Array)
            {
                updatedJson = json;
                return false;
            }

            var existingNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in toolsElement.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.Object &&
                    tool.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String)
                {
                    var name = nameElement.GetString();
                    if (!string.IsNullOrEmpty(name))
                        existingNames.Add(name);
                }
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals("result"))
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("result");
                    writer.WriteStartObject();
                    foreach (var resultProperty in result.EnumerateObject())
                    {
                        if (resultProperty.NameEquals("tools"))
                        {
                            writer.WritePropertyName("tools");
                            writer.WriteStartArray();
                            foreach (var tool in toolsElement.EnumerateArray())
                                tool.WriteTo(writer);
                            WriteTools(writer, existingNames);
                            writer.WriteEndArray();
                        }
                        else
                        {
                            resultProperty.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.Flush();
            }

            updatedJson = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            updatedJson = json;
            return false;
        }
    }

    public static void WriteTools(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        WriteStatusTool(writer, existingNames);
        WriteBuildTool(writer, existingNames);
        WriteRestartTool(writer, existingNames);
        WriteDeployTool(writer, existingNames);
        WriteWaitForOperationTool(writer, existingNames);
        WriteLogsTool(writer, existingNames);
        WriteTracesTool(writer, existingNames);
        WriteConsoleLogsTool(writer, existingNames);
        WriteTraceLogsTool(writer, existingNames);
    }

    private static void WriteStatusTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        if (existingNames?.Contains(StatusToolName) == true)
            return;

        writer.WriteStartObject();
        writer.WriteString("name", StatusToolName);
        writer.WriteString("title", "Harness Status");
        writer.WriteString("description",
            "Check the current state of the RepoQL host and harness session. " +
            "Call this FIRST when starting a session or when unsure about host state. " +
            "Returns: session_id, host state (ready/stopped/starting/unknown), Aspire connection status, " +
            "and any in-progress operation. If host is stopped, use harness.restart. " +
            "If Aspire is disconnected, lifecycle tools won't work.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", "Harness Status");
        writer.WriteBoolean("readOnlyHint", true);
        writer.WriteBoolean("destructiveHint", false);
        writer.WriteBoolean("idempotentHint", true);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteBuildTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        WriteActionTool(
            writer,
            existingNames,
            BuildToolName,
            "Harness Build",
            "Rebuild and restart the RepoQL host via Aspire (~10s). " +
            "Use after making code changes to RepoQL. Calls Aspire's rebuild_and_restart command " +
            "which builds in-place and restarts. Returns success/failure with duration. " +
            "On failure, check the 'output' field and harness.console_logs() for compiler errors. " +
            "Blocks other tool calls during build. Prefer this over harness.deploy for fast iteration.",
            destructiveHint: true);
    }

    private static void WriteRestartTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        WriteActionTool(
            writer,
            existingNames,
            RestartToolName,
            "Harness Restart",
            "Restart the RepoQL host without rebuilding (~2-3s). " +
            "Use when the host is stopped or unresponsive but no code changes are needed. " +
            "Also use after a crash to recover. Does NOT rebuild — if you changed code, use harness.build instead.",
            destructiveHint: true);
    }

    private static void WriteDeployTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        WriteActionTool(
            writer,
            existingNames,
            DeployToolName,
            "Harness Deploy",
            "Full publish and deploy cycle (~30s): dotnet publish → copy artifacts → restart. " +
            "Use when you need a clean, self-contained deployment (e.g., testing publish output). " +
            "For normal development iteration, prefer harness.build which is much faster.",
            destructiveHint: true);
    }

    private static void WriteWaitForOperationTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        if (existingNames?.Contains(WaitForOperationToolName) == true)
            return;

        writer.WriteStartObject();
        writer.WriteString("name", WaitForOperationToolName);
        writer.WriteString("title", "Harness Wait For Operation");
        writer.WriteString("description",
            "Block until a build/deploy/restart operation completes (5min timeout). " +
            "Use when another session is building and you got an 'operation_in_progress' error. " +
            "Returns immediately if no operation is in progress.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", "Harness Wait For Operation");
        writer.WriteBoolean("readOnlyHint", true);
        writer.WriteBoolean("destructiveHint", false);
        writer.WriteBoolean("idempotentHint", true);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteLogsTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        if (existingNames?.Contains(LogsToolName) == true)
            return;

        writer.WriteStartObject();
        writer.WriteString("name", LogsToolName);
        writer.WriteString("title", "Harness Logs");
        writer.WriteString("description",
            "Query structured logs (ILogger output) from the RepoQL host via Aspire. " +
            "Use for investigating errors, understanding behavior, and checking indexing progress. " +
            "For crash debugging or startup failures, prefer harness.console_logs which captures " +
            "raw stdout/stderr including unhandled exceptions. " +
            "To investigate a specific trace, use harness.trace_logs instead.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("since");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Time window: '30s', '5m', '1h', or ISO timestamp.");
        writer.WriteEndObject();
        writer.WritePropertyName("level");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Minimum severity: debug, info, warning, error.");
        writer.WriteEndObject();
        writer.WritePropertyName("contains");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Text filter applied to log messages.");
        writer.WriteEndObject();
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Aspire resource name (default: host).");
        writer.WriteEndObject();
        writer.WritePropertyName("limit");
        writer.WriteStartObject();
        writer.WriteString("type", "integer");
        writer.WriteString("description", "Max results (default 100, max 1000).");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", "Harness Logs");
        writer.WriteBoolean("readOnlyHint", true);
        writer.WriteBoolean("destructiveHint", false);
        writer.WriteBoolean("idempotentHint", true);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTracesTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        if (existingNames?.Contains(TracesToolName) == true)
            return;

        writer.WriteStartObject();
        writer.WriteString("name", TracesToolName);
        writer.WriteString("title", "Harness Traces");
        writer.WriteString("description",
            "Query distributed traces from the RepoQL host via Aspire. " +
            "Shows operation timings, spans, and error status. " +
            "Use has_error: true to find failed operations. " +
            "To drill into a specific trace, copy its trace_id and call harness.trace_logs.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("since");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Time window: '30s', '5m', '1h', or ISO timestamp.");
        writer.WriteEndObject();
        writer.WritePropertyName("has_error");
        writer.WriteStartObject();
        writer.WriteString("type", "boolean");
        writer.WriteString("description", "Filter to traces that contain errors.");
        writer.WriteEndObject();
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Aspire resource name (default: host).");
        writer.WriteEndObject();
        writer.WritePropertyName("limit");
        writer.WriteStartObject();
        writer.WriteString("type", "integer");
        writer.WriteString("description", "Max results (default 10, max 100).");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", "Harness Traces");
        writer.WriteBoolean("readOnlyHint", true);
        writer.WriteBoolean("destructiveHint", false);
        writer.WriteBoolean("idempotentHint", true);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteConsoleLogsTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        if (existingNames?.Contains(ConsoleLogsToolName) == true)
            return;

        writer.WriteStartObject();
        writer.WriteString("name", ConsoleLogsToolName);
        writer.WriteString("title", "Harness Console Logs");
        writer.WriteString("description",
            "Get raw console output (stdout/stderr) from a resource. " +
            "Use for crash debugging, startup failures, and unhandled exceptions that bypass structured logging. " +
            "When the host crashes or won't start, check console_logs FIRST — " +
            "the root cause is usually in stderr before structured logging initializes.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Aspire resource name (default: host).");
        writer.WriteEndObject();
        writer.WritePropertyName("contains");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "Case-insensitive text filter. Only lines containing this text are returned. Use 'error', 'exception', 'fail' to find crash-relevant output.");
        writer.WriteEndObject();
        writer.WritePropertyName("limit");
        writer.WriteStartObject();
        writer.WriteString("type", "integer");
        writer.WriteString("description", "Max lines to return (default 200, max 2000). Returns most recent matching lines.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", "Harness Console Logs");
        writer.WriteBoolean("readOnlyHint", true);
        writer.WriteBoolean("destructiveHint", false);
        writer.WriteBoolean("idempotentHint", true);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTraceLogsTool(Utf8JsonWriter writer, HashSet<string>? existingNames)
    {
        if (existingNames?.Contains(TraceLogsToolName) == true)
            return;

        writer.WriteStartObject();
        writer.WriteString("name", TraceLogsToolName);
        writer.WriteString("title", "Harness Trace Logs");
        writer.WriteString("description",
            "Get structured logs for a specific distributed trace. " +
            "Use after harness.traces to drill into a specific operation and see what happened. " +
            "The trace_id comes from the harness.traces response.");
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WritePropertyName("trace_id");
        writer.WriteStartObject();
        writer.WriteString("type", "string");
        writer.WriteString("description", "The trace ID to get logs for (from harness.traces output).");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WritePropertyName("required");
        writer.WriteStartArray();
        writer.WriteStringValue("trace_id");
        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", "Harness Trace Logs");
        writer.WriteBoolean("readOnlyHint", true);
        writer.WriteBoolean("destructiveHint", false);
        writer.WriteBoolean("idempotentHint", true);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteActionTool(
        Utf8JsonWriter writer,
        HashSet<string>? existingNames,
        string name,
        string title,
        string description,
        bool destructiveHint)
    {
        if (existingNames?.Contains(name) == true)
            return;

        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("title", title);
        writer.WriteString("description", description);
        writer.WritePropertyName("inputSchema");
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WritePropertyName("properties");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.WritePropertyName("annotations");
        writer.WriteStartObject();
        writer.WriteString("title", title);
        writer.WriteBoolean("readOnlyHint", false);
        writer.WriteBoolean("destructiveHint", destructiveHint);
        writer.WriteBoolean("idempotentHint", false);
        writer.WriteBoolean("openWorldHint", false);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
