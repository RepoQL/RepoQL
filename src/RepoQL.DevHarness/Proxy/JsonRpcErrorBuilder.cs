using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Builds JSON-RPC error responses with raw id preservation.
/// Complexity: Focused serialization helper to keep error shape consistent.
/// </summary>
internal static class JsonRpcErrorBuilder
{
    public static string BuildError(string rawId, int code, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            using (var idDoc = JsonDocument.Parse(rawId))
            {
                idDoc.RootElement.WriteTo(writer);
            }

            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
