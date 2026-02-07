using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Represents a JSON-RPC id with a stable dictionary key and raw JSON for round-tripping.
/// Complexity: Encapsulates type-safe id handling so proxy logic stays focused on routing.
/// </summary>
internal readonly record struct JsonRpcId(string Key, string RawJson)
{
    public static bool TryParse(JsonElement element, out JsonRpcId id)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            id = default;
            return false;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (value is null)
            {
                id = default;
                return false;
            }

            id = new JsonRpcId($"s:{value}", element.GetRawText());
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            var raw = element.GetRawText();
            id = new JsonRpcId($"n:{raw}", raw);
            return true;
        }

        var fallback = element.GetRawText();
        id = new JsonRpcId($"o:{fallback}", fallback);
        return true;
    }
}
