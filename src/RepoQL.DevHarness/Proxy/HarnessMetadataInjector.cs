using System.Text;
using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Injects _harness metadata into successful tool call responses.
/// Complexity: Rewrites only the result object while preserving the rest of the JSON-RPC envelope.
/// </summary>
internal static class HarnessMetadataInjector
{
    public static bool TryInjectToolResponse(string json, string requestId, long durationMs, out string updatedJson)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("error", out _))
        {
            updatedJson = json;
            return false;
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            updatedJson = json;
            return false;
        }

        if (result.TryGetProperty("_harness", out _))
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
                foreach (var resultProperty in result.EnumerateObject())
                {
                    resultProperty.WriteTo(writer);
                }

                writer.WritePropertyName("_harness");
                writer.WriteStartObject();
                writer.WriteString("request_id", requestId);
                writer.WriteNumber("duration_ms", durationMs);
                writer.WriteEndObject();

                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        updatedJson = Encoding.UTF8.GetString(stream.ToArray());
        return true;
    }
}
