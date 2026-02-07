using System.Text.Json;

namespace RepoQL.DevHarness.Proxy;

/// <summary>
/// Purpose: Extract and parse Aspire resource lists from MCP tool output.
/// Complexity: Handles embedded JSON arrays using shared extraction.
/// </summary>
internal static class AspireResourceParser
{
    public static IReadOnlyList<AspireResource> Parse(string? text)
    {
        if (!AspireJsonExtractor.TryExtract(text, out var json))
            return Array.Empty<AspireResource>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<AspireResource>();

        var resources = new List<AspireResource>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
                continue;

            var name = TryGetString(element, "resource_name") ?? TryGetString(element, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var state = TryGetString(element, "state");
            resources.Add(new AspireResource(name, state));
        }

        return resources;
    }

    private static string? TryGetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            return property.GetString();

        foreach (var entry in element.EnumerateObject())
        {
            if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase) &&
                entry.Value.ValueKind == JsonValueKind.String)
                return entry.Value.GetString();
        }

        return null;
    }
}

/// <summary>
/// Purpose: Represent a resource entry returned by Aspire's list_resources tool.
/// Complexity: Minimal data carrier for name and state.
/// </summary>
internal readonly record struct AspireResource(string Name, string? State);
