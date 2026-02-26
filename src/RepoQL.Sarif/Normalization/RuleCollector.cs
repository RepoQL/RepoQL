using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoQL.Sarif.Normalization;

/// <summary>
/// Collects rule metadata from driver and extension tool components.
/// </summary>
public sealed class RuleCollector
{
    /// <summary>
    /// Build a run-local lookup of rule id to descriptor.
    /// Driver rules override extension rules on collision.
    /// </summary>
    public IReadOnlyDictionary<string, RuleDescriptor> Collect(JsonElement run)
    {
        var rules = new Dictionary<string, RuleDescriptor>(StringComparer.Ordinal);

        foreach (var extensionRule in EnumerateExtensionRules(run))
        {
            if (TryBuildDescriptor(extensionRule, out var descriptor))
                rules[descriptor.Id] = descriptor;
        }

        foreach (var driverRule in EnumerateDriverRules(run))
        {
            if (TryBuildDescriptor(driverRule, out var descriptor))
                rules[descriptor.Id] = descriptor;
        }

        return rules;
    }

    private static IEnumerable<JsonElement> EnumerateDriverRules(JsonElement run)
    {
        if (!TryGetToolDriver(run, out var driver))
            yield break;

        if (!driver.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var rule in rules.EnumerateArray())
            if (rule.ValueKind == JsonValueKind.Object)
                yield return rule;
    }

    private static IEnumerable<JsonElement> EnumerateExtensionRules(JsonElement run)
    {
        if (!run.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.Object)
            yield break;

        if (!tool.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var extension in extensions.EnumerateArray())
        {
            if (extension.ValueKind != JsonValueKind.Object)
                continue;

            if (!extension.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var rule in rules.EnumerateArray())
                if (rule.ValueKind == JsonValueKind.Object)
                    yield return rule;
        }
    }

    private static bool TryBuildDescriptor(JsonElement rule, out RuleDescriptor descriptor)
    {
        descriptor = default!;
        if (!TryGetString(rule, "id", out var id))
            return false;

        string? defaultLevel = null;
        if (rule.TryGetProperty("defaultConfiguration", out var defaultConfiguration)
            && defaultConfiguration.ValueKind == JsonValueKind.Object
            && TryGetString(defaultConfiguration, "level", out var level))
        {
            defaultLevel = level;
        }

        var messageStrings = CollectMessageStrings(rule);
        var properties = TryGetJsonObject(rule, "properties");
        descriptor = new RuleDescriptor(
            id,
            defaultLevel,
            messageStrings,
            BuildRuleMetadata(rule, properties),
            properties);

        return true;
    }

    private static IReadOnlyDictionary<string, string> CollectMessageStrings(JsonElement rule)
    {
        if (!rule.TryGetProperty("messageStrings", out var messageStrings) || messageStrings.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in messageStrings.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (TryGetString(entry.Value, "text", out var text))
            {
                resolved[entry.Name] = text;
                continue;
            }

            if (TryGetString(entry.Value, "markdown", out var markdown))
                resolved[entry.Name] = markdown;
        }

        return resolved;
    }

    private static JsonObject? BuildRuleMetadata(JsonElement rule, JsonObject? properties)
    {
        var metadata = new JsonObject();

        if (TryGetString(rule, "name", out var name))
            metadata["name"] = name;
        else if (TryGetNestedString(rule, "shortDescription", "text", out var shortDescription))
            metadata["name"] = shortDescription;

        if (TryGetNestedString(rule, "fullDescription", "text", out var fullDescription))
            metadata["description"] = fullDescription;

        if (TryGetString(rule, "helpUri", out var helpUri))
            metadata["helpUri"] = helpUri;

        if (TryGetNestedString(rule, "help", "markdown", out var helpMarkdown))
            metadata["helpMarkdown"] = helpMarkdown;

        if (properties is not null)
        {
            if (properties.TryGetPropertyValue("tags", out var tags) && tags is JsonArray)
                metadata["tags"] = tags.DeepClone();

            if (properties.TryGetPropertyValue("cwe", out var cwe) && cwe is not null)
                metadata["cwe"] = cwe.DeepClone();

            metadata["properties"] = properties.DeepClone();
        }

        if (metadata.Count == 0)
            return null;

        return metadata;
    }

    private static JsonObject? TryGetJsonObject(JsonElement owner, string propertyName)
    {
        if (!owner.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        return JsonNode.Parse(value.GetRawText()) as JsonObject;
    }

    private static bool TryGetToolDriver(JsonElement run, out JsonElement driver)
    {
        driver = default;
        return run.TryGetProperty("tool", out var tool)
               && tool.ValueKind == JsonValueKind.Object
               && tool.TryGetProperty("driver", out driver)
               && driver.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetString(JsonElement owner, string propertyName, out string value)
    {
        value = string.Empty;
        if (!owner.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        var str = property.GetString();
        if (string.IsNullOrWhiteSpace(str))
            return false;

        value = str;
        return true;
    }

    private static bool TryGetNestedString(JsonElement owner, string objectName, string propertyName, out string value)
    {
        value = string.Empty;
        return owner.TryGetProperty(objectName, out var inner)
               && inner.ValueKind == JsonValueKind.Object
               && TryGetString(inner, propertyName, out value);
    }
}
