using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoQL.Mcp.Client;

/// <summary>
/// Generates DuckDB SQL macros for MCP tools, enabling natural SQL syntax like:
/// SELECT * FROM aspire_dashboard_list_resources()
/// </summary>
public static partial class McpMacroGenerator
{
    /// <summary>
    /// Generates all SQL macros for the given tools.
    /// </summary>
    public static string GenerateMacros(IReadOnlyList<McpToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- MCP Tool Macros (auto-generated)");
        sb.AppendLine();

        foreach (var tool in tools)
        {
            sb.AppendLine(GenerateToolMacro(tool));
            sb.AppendLine();
        }

        // Generate discovery macro
        sb.AppendLine(GenerateDiscoveryMacro(tools));

        return sb.ToString();
    }

    /// <summary>
    /// Generates a single tool macro.
    /// </summary>
    public static string GenerateToolMacro(McpToolDefinition tool)
    {
        var macroName = SanitizeName($"{tool.ServerName}_{tool.ToolName}");
        var parameters = ExtractParameters(tool.InputSchema);

        var paramList = parameters.Count > 0
            ? string.Join(", ", parameters.Select(p => $"{p.Name} := NULL"))
            : "";

        var paramsJsonExpr = BuildParamsJsonExpression(parameters);

        // Generate macro that:
        // 1. Calls _mcp_call_internal to get JSON result (UDF handles JSON extraction from markdown)
        // 2. Parses the JSON as an array and unnests into rows
        // 3. Each row is a JSON object that can be queried with json_extract
        return $$"""
            CREATE OR REPLACE MACRO {{macroName}}({{paramList}}) AS TABLE (
                WITH raw_result AS (
                    SELECT _mcp_call_internal('{{EscapeSql(tool.ServerName)}}', '{{EscapeSql(tool.ToolName)}}', {{paramsJsonExpr}}) AS json_data
                ),
                parsed AS (
                    SELECT
                        CASE
                            WHEN json_type(json_data::JSON) = 'ARRAY' THEN json_data
                            WHEN json_data IS NULL OR json_data = 'null' THEN '[]'
                            ELSE '[' || json_data || ']'
                        END AS json_array
                    FROM raw_result
                )
                SELECT unnest(from_json(json_array, '["json"]')) AS value
                FROM parsed
                WHERE json_array != '[]'
            );
            """;
    }

    /// <summary>
    /// Generates a macro that lists all available MCP tools.
    /// </summary>
    public static string GenerateDiscoveryMacro(IReadOnlyList<McpToolDefinition> tools)
    {
        if (tools.Count == 0)
        {
            return """
                CREATE OR REPLACE MACRO mcp_tools() AS TABLE (
                    SELECT NULL::VARCHAR AS server, NULL::VARCHAR AS tool, NULL::VARCHAR AS macro_name, NULL::VARCHAR AS description
                    WHERE false
                );
                """;
        }

        var values = tools.Select(t =>
        {
            var macroName = SanitizeName($"{t.ServerName}_{t.ToolName}");
            var desc = EscapeSql(t.Description ?? "");
            return $"('{EscapeSql(t.ServerName)}', '{EscapeSql(t.ToolName)}', '{macroName}', '{desc}')";
        });

        return $$"""
            CREATE OR REPLACE MACRO mcp_tools() AS TABLE (
                SELECT * FROM (VALUES
                    {{string.Join(",\n            ", values)}}
                ) AS t(server, tool, macro_name, description)
            );
            """;
    }

    /// <summary>
    /// Extracts parameter definitions from a JSON Schema.
    /// </summary>
    internal static IReadOnlyList<McpToolParameter> ExtractParameters(JsonElement? inputSchema)
    {
        if (inputSchema is null || inputSchema.Value.ValueKind != JsonValueKind.Object)
            return [];

        var schema = inputSchema.Value;

        // Get properties from the schema
        if (!schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return [];

        // Get required fields if present
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schema.TryGetProperty("required", out var requiredArray) &&
            requiredArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in requiredArray.EnumerateArray())
            {
                if (req.ValueKind == JsonValueKind.String)
                    required.Add(req.GetString()!);
            }
        }

        var parameters = new List<McpToolParameter>();
        foreach (var prop in properties.EnumerateObject())
        {
            var paramName = SanitizeName(prop.Name);
            var propSchema = prop.Value;

            var paramType = "string";
            if (propSchema.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String)
            {
                paramType = typeProp.GetString() ?? "string";
            }

            string? description = null;
            if (propSchema.TryGetProperty("description", out var descProp) &&
                descProp.ValueKind == JsonValueKind.String)
            {
                description = descProp.GetString();
            }

            JsonElement? defaultValue = null;
            if (propSchema.TryGetProperty("default", out var defaultProp))
            {
                defaultValue = defaultProp;
            }

            parameters.Add(new McpToolParameter
            {
                Name = paramName,
                Type = paramType,
                Required = required.Contains(prop.Name),
                Description = description,
                Default = defaultValue
            });
        }

        // Sort: required parameters first, then optional
        return parameters
            .OrderByDescending(p => p.Required)
            .ThenBy(p => p.Name)
            .ToList();
    }

    /// <summary>
    /// Builds a SQL expression that constructs a JSON object from macro parameters.
    /// </summary>
    private static string BuildParamsJsonExpression(IReadOnlyList<McpToolParameter> parameters)
    {
        if (parameters.Count == 0)
        {
            // IMPORTANT: Must return non-NULL string - DuckDB skips UDF calls when all args are NULL
            return "'{}'";
        }

        // Build a CASE expression that constructs JSON only for non-NULL parameters
        // This uses json_object which handles nulls gracefully
        var jsonPairs = parameters.Select(p =>
            $"'{p.Name}', {p.Name}");

        // Use json_object with ABSENT ON NULL to omit null values
        // DuckDB syntax: json_object('key1', val1, 'key2', val2, ...)
        // COALESCE ensures we never pass NULL to the UDF
        return $"COALESCE(json_object({string.Join(", ", jsonPairs)})::VARCHAR, '{{}}')";
    }

    /// <summary>
    /// Sanitizes a name for use as a SQL identifier.
    /// </summary>
    internal static string SanitizeName(string name)
    {
        // Replace hyphens and other non-identifier chars with underscores
        var sanitized = InvalidCharsRegex().Replace(name, "_");

        // Ensure it starts with a letter or underscore
        if (sanitized.Length > 0 && char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;

        // Lowercase for consistency
        return sanitized.ToLowerInvariant();
    }

    /// <summary>
    /// Escapes a string for use in SQL.
    /// </summary>
    private static string EscapeSql(string value)
    {
        return value.Replace("'", "''");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex InvalidCharsRegex();
}
