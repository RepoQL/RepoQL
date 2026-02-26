using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoQL.Sarif.Normalization;

/// <summary>
/// Resolves SARIF result severity and extracts tool-specific severity metadata.
/// </summary>
public sealed class SeverityResolver
{
    /// <summary>
    /// Resolve severity level with cascade: result.level > rule default level > warning.
    /// </summary>
    public string ResolveLevel(JsonElement result, RuleDescriptor? rule)
    {
        if (TryGetString(result, "level", out var resultLevel))
            return NormalizeLevel(resultLevel);

        if (!string.IsNullOrWhiteSpace(rule?.DefaultLevel))
            return NormalizeLevel(rule.DefaultLevel!);

        return "warning";
    }

    /// <summary>
    /// Extract tool-specific severity fields into a JSON object for payload storage.
    /// </summary>
    public JsonObject? ExtractToolSpecificSeverity(JsonElement result, RuleDescriptor? rule)
    {
        var values = new JsonObject();

        if (TryGetResultProperty(result, "ideaSeverity", out var ideaSeverity))
            values["ideaSeverity"] = ideaSeverity;

        if (TryGetResultProperty(result, "qodanaSeverity", out var qodanaSeverity))
            values["qodanaSeverity"] = qodanaSeverity;

        if (TryGetResultProperty(result, "severity", out var sonarSeverity))
            values["sonarSeverity"] = sonarSeverity;

        if (TryGetResultProperty(result, "type", out var sonarType))
            values["sonarType"] = sonarType;

        if (TryGetResultProperty(result, "security-severity", out var resultSecuritySeverity))
            values["securitySeverity"] = resultSecuritySeverity;
        else if (TryGetRuleProperty(rule, "security-severity", out var ruleSecuritySeverity))
            values["securitySeverity"] = ruleSecuritySeverity;

        return values.Count == 0 ? null : values;
    }

    private static string NormalizeLevel(string level)
    {
        return level.Trim().ToLowerInvariant();
    }

    private static bool TryGetResultProperty(JsonElement result, string propertyName, out string value)
    {
        value = string.Empty;
        if (!result.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return false;

        if (!properties.TryGetProperty(propertyName, out var property))
            return false;

        return TryElementToString(property, out value);
    }

    private static bool TryGetRuleProperty(RuleDescriptor? rule, string propertyName, out string value)
    {
        value = string.Empty;
        if (rule?.Properties is null)
            return false;

        if (!rule.Properties.TryGetPropertyValue(propertyName, out var property) || property is null)
            return false;

        return TryNodeToString(property, out value);
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

    private static bool TryElementToString(JsonElement element, out string value)
    {
        value = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = element.GetRawText();
                break;
            default:
                return false;
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryNodeToString(JsonNode node, out string value)
    {
        value = string.Empty;
        if (node is JsonValue scalar)
        {
            if (scalar.TryGetValue(out string? stringValue) && !string.IsNullOrWhiteSpace(stringValue))
            {
                value = stringValue;
                return true;
            }

            if (scalar.TryGetValue(out int intValue))
            {
                value = intValue.ToString();
                return true;
            }

            if (scalar.TryGetValue(out long longValue))
            {
                value = longValue.ToString();
                return true;
            }

            if (scalar.TryGetValue(out decimal decimalValue))
            {
                value = decimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            if (scalar.TryGetValue(out double doubleValue))
            {
                value = doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            if (scalar.TryGetValue(out bool boolValue))
            {
                value = boolValue ? "true" : "false";
                return true;
            }
        }

        return false;
    }
}
