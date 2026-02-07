using System.Text;
using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Transform Aspire telemetry into token-efficient payloads for AI agents.
/// Complexity: Extracts signal from verbose Aspire output. Traces get summarized (root + errors + slow spans),
/// logs get stripped to essential fields, console logs get tail + optional text filtering.
/// </summary>
internal static class HarnessTelemetryFormatter
{
    private const int SignificantSpanDurationMs = 50;

    public static bool TryFormatLogs(string? text, int limit, out string payloadJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            payloadJson = BuildEmptyCollection("logs");
            return true;
        }

        if (!AspireJsonExtractor.TryExtract(text, out var json))
        {
            payloadJson = string.Empty;
            error = "Aspire logs response was not valid JSON.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root : TryFindArray(root, "logs");

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("logs");
                writer.WriteStartArray();

                var count = 0;
                var truncated = false;

                if (items.HasValue && items.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.Value.EnumerateArray())
                    {
                        if (count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        WriteLogEntry(writer, item);
                        count++;
                    }
                }

                writer.WriteEndArray();
                writer.WriteNumber("count", count);
                writer.WriteBoolean("truncated", truncated);
                writer.WriteEndObject();
                writer.Flush();
            }

            payloadJson = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            payloadJson = string.Empty;
            error = "Aspire logs response was not valid JSON.";
            return false;
        }
    }

    public static bool TryFormatTraces(string? text, int limit, out string payloadJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            payloadJson = BuildEmptyCollection("traces");
            return true;
        }

        if (!AspireJsonExtractor.TryExtract(text, out var json))
        {
            payloadJson = string.Empty;
            error = "Aspire traces response was not valid JSON.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root : TryFindArray(root, "traces");

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("traces");
                writer.WriteStartArray();

                var count = 0;
                var truncated = false;

                if (items.HasValue && items.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.Value.EnumerateArray())
                    {
                        if (count >= limit)
                        {
                            truncated = true;
                            break;
                        }

                        WriteTraceSummary(writer, item);
                        count++;
                    }
                }

                writer.WriteEndArray();
                writer.WriteNumber("count", count);
                writer.WriteBoolean("truncated", truncated);
                writer.WriteEndObject();
                writer.Flush();
            }

            payloadJson = Encoding.UTF8.GetString(stream.ToArray());
            return true;
        }
        catch (JsonException)
        {
            payloadJson = string.Empty;
            error = "Aspire traces response was not valid JSON.";
            return false;
        }
    }

    public static bool TryFormatConsoleLogs(string? text, int limit, out string payloadJson, out string? error)
        => TryFormatConsoleLogs(text, limit, null, out payloadJson, out error);

    public static bool TryFormatConsoleLogs(string? text, int limit, string? contains, out string payloadJson, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            payloadJson = BuildEmptyConsoleOutput();
            return true;
        }

        var lines = text.Split('\n');

        // Apply text filter first if specified.
        IEnumerable<string> filtered = lines.Select(l => l.TrimEnd('\r'));
        var totalBeforeFilter = lines.Length;

        if (!string.IsNullOrWhiteSpace(contains))
            filtered = filtered.Where(l => l.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var filteredArray = filtered.Where(l => !string.IsNullOrEmpty(l)).ToArray();

        // Take the last N lines (most recent).
        var startIndex = Math.Max(0, filteredArray.Length - limit);
        var selectedLines = filteredArray[startIndex..];

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("lines");
            writer.WriteStartArray();
            foreach (var line in selectedLines)
                writer.WriteStringValue(line);
            writer.WriteEndArray();
            writer.WriteNumber("count", selectedLines.Length);
            writer.WriteBoolean("truncated", startIndex > 0);
            if (!string.IsNullOrWhiteSpace(contains))
                writer.WriteNumber("total_before_filter", totalBeforeFilter);
            writer.WriteEndObject();
            writer.Flush();
        }

        payloadJson = Encoding.UTF8.GetString(stream.ToArray());
        return true;
    }

    /// <summary>
    /// Extract only the fields that matter for debugging: timestamp, severity, message, exception, source.
    /// Drops: dashboard_link, log_id, span_id, resource_name, and low-value attributes.
    /// </summary>
    private static void WriteLogEntry(Utf8JsonWriter writer, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            entry.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();

        if (entry.TryGetProperty("timestamp", out var ts))
            writer.WriteString("timestamp", ts.GetString());

        if (entry.TryGetProperty("severity", out var sev))
            writer.WriteString("severity", sev.GetString());

        if (entry.TryGetProperty("message", out var msg))
            writer.WriteString("message", msg.GetString());

        if (entry.TryGetProperty("source", out var src))
            writer.WriteString("source", src.GetString());

        if (entry.TryGetProperty("exception", out var exc) && exc.ValueKind == JsonValueKind.String)
            writer.WriteString("exception", exc.GetString());

        // Only include attributes if they contain useful info (not empty object).
        if (entry.TryGetProperty("attributes", out var attrs) &&
            attrs.ValueKind == JsonValueKind.Object)
        {
            var hasContent = false;
            foreach (var _ in attrs.EnumerateObject())
            {
                hasContent = true;
                break;
            }

            if (hasContent)
            {
                writer.WritePropertyName("attributes");
                attrs.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Summarize a trace: header fields + root span + error spans + slow spans.
    /// Collapses the rest into a count. Typical reduction: 30 spans → 3-5 shown.
    /// </summary>
    private static void WriteTraceSummary(Utf8JsonWriter writer, JsonElement trace)
    {
        if (trace.ValueKind != JsonValueKind.Object)
        {
            trace.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();

        // Trace header.
        if (trace.TryGetProperty("trace_id", out var traceId))
            writer.WriteString("trace_id", traceId.GetString());
        if (trace.TryGetProperty("title", out var title))
            writer.WriteString("title", title.GetString());
        if (trace.TryGetProperty("duration_ms", out var duration))
            writer.WriteNumber("duration_ms", duration.GetInt64());
        if (trace.TryGetProperty("has_error", out var hasError))
            writer.WriteBoolean("has_error", hasError.GetBoolean());
        if (trace.TryGetProperty("timestamp", out var ts))
            writer.WriteString("timestamp", ts.GetString());

        // Summarize spans: root + errors + significant duration.
        if (trace.TryGetProperty("spans", out var spans) && spans.ValueKind == JsonValueKind.Array)
        {
            var totalSpans = 0;
            var shownSpans = new List<JsonElement>();

            foreach (var span in spans.EnumerateArray())
            {
                totalSpans++;

                if (span.ValueKind != JsonValueKind.Object)
                    continue;

                // Always include: root spans (no parent), error spans, slow spans.
                var isRoot = !span.TryGetProperty("parent_span_id", out var parentId) ||
                             parentId.ValueKind == JsonValueKind.Null ||
                             string.IsNullOrEmpty(parentId.GetString());
                var isError = span.TryGetProperty("status", out var status) &&
                              status.ValueKind == JsonValueKind.String &&
                              string.Equals(status.GetString(), "Error", StringComparison.OrdinalIgnoreCase);
                var isSlow = span.TryGetProperty("duration_ms", out var spanDuration) &&
                             spanDuration.TryGetInt64(out var durationMs) &&
                             durationMs >= SignificantSpanDurationMs;

                if (isRoot || isError || isSlow)
                    shownSpans.Add(span);
            }

            writer.WritePropertyName("spans");
            writer.WriteStartArray();
            foreach (var span in shownSpans)
                WriteSpanSummary(writer, span);
            writer.WriteEndArray();

            var omitted = totalSpans - shownSpans.Count;
            writer.WriteNumber("total_spans", totalSpans);
            if (omitted > 0)
                writer.WriteNumber("omitted_spans", omitted);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Write span with only the fields that matter: name, duration, status, kind, and key attributes.
    /// Drops: links, back_links, source/destination (usually just "host").
    /// </summary>
    private static void WriteSpanSummary(Utf8JsonWriter writer, JsonElement span)
    {
        writer.WriteStartObject();

        if (span.TryGetProperty("name", out var name))
            writer.WriteString("name", name.GetString());
        if (span.TryGetProperty("span_id", out var spanId))
            writer.WriteString("span_id", spanId.GetString());
        if (span.TryGetProperty("kind", out var kind))
            writer.WriteString("kind", kind.GetString());
        if (span.TryGetProperty("duration_ms", out var duration))
            writer.WriteNumber("duration_ms", duration.GetInt64());
        if (span.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            writer.WriteString("status", status.GetString());
        if (span.TryGetProperty("status_message", out var statusMsg) && statusMsg.ValueKind == JsonValueKind.String)
            writer.WriteString("status_message", statusMsg.GetString());

        // Include attributes if non-empty.
        if (span.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
        {
            var hasContent = false;
            foreach (var _ in attrs.EnumerateObject())
            {
                hasContent = true;
                break;
            }

            if (hasContent)
            {
                writer.WritePropertyName("attributes");
                attrs.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static JsonElement? TryFindArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array)
                return property;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
                return property.Value;
        }

        return null;
    }

    private static string BuildEmptyCollection(string name)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteNumber("count", 0);
            writer.WriteBoolean("truncated", false);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildEmptyConsoleOutput()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("lines");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteNumber("count", 0);
            writer.WriteBoolean("truncated", false);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
