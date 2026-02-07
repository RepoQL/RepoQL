using System.Text;
using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Build MCP tool responses for harness-generated payloads.
/// Complexity: Centralizes JSON-RPC tool response shaping with optional error flag.
/// </summary>
internal static class HarnessToolResponseBuilder
{
    public static string BuildToolResponse(JsonRpcId id, string payloadJson, string requestId, long durationMs, bool isError)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            using (var idDoc = JsonDocument.Parse(id.RawJson))
            {
                idDoc.RootElement.WriteTo(writer);
            }

            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", payloadJson);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("isError", isError);
            writer.WritePropertyName("_harness");
            writer.WriteStartObject();
            writer.WriteString("request_id", requestId);
            writer.WriteNumber("duration_ms", durationMs);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
