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
            ? string.Join(", ", parameters.Select(p => $"\"{p.Name}\" := NULL"))
            : "";

        var paramsJsonExpr = BuildParamsJsonExpression(parameters);

        // Generate macro that:
        // 1. Calls _mcp_call_internal to get raw response text
        // 2. Uses convert_to_json() UDF to normalize response payloads
        //    (handles JSON/JSONL/CSV/TSV/YAML/embedded)
        // 3. Writes JSON to temp file and uses read_json_auto for dynamic column detection
        // Note: DuckDB table functions cannot contain subqueries, so we chain the temp file write
        // directly with the scalar UDFs to avoid subquery issues
        return $$"""
            CREATE OR REPLACE MACRO {{macroName}}({{paramList}}) AS TABLE (
                SELECT * FROM read_json_auto(
                    _write_temp_json(
                        convert_to_json(
                            _mcp_call_internal('{{EscapeSql(tool.ServerName)}}', '{{EscapeSql(tool.ToolName)}}', {{paramsJsonExpr}}),
                            'true'
                        )
                    ),
                    maximum_object_size := 67108864
                )
            );
            """;
    }

    /// <summary>
    /// Generates macros that list all available MCP tools and their parameters.
    /// </summary>
    public static string GenerateDiscoveryMacro(IReadOnlyList<McpToolDefinition> tools)
    {
        var sb = new StringBuilder();

        // Generate mcp_tools() macro with example usage
        if (tools.Count == 0)
        {
            sb.AppendLine("""
                CREATE OR REPLACE MACRO mcp_tools() AS TABLE (
                    SELECT NULL::VARCHAR AS server, NULL::VARCHAR AS tool, NULL::VARCHAR AS macro_name,
                           NULL::VARCHAR AS description, NULL::VARCHAR AS example
                    WHERE false
                );
                """);
        }
        else
        {
            var toolValues = tools.Select(t =>
            {
                var macroName = SanitizeName($"{t.ServerName}_{t.ToolName}");
                var desc = EscapeSql(t.Description ?? "");
                var parameters = ExtractParameters(t.InputSchema);
                var example = BuildExampleUsage(macroName, parameters);
                return $"('{EscapeSql(t.ServerName)}', '{EscapeSql(t.ToolName)}', '{macroName}', '{desc}', '{EscapeSql(example)}')";
            });

            sb.AppendLine($$"""
                CREATE OR REPLACE MACRO mcp_tools() AS TABLE (
                    SELECT * FROM (VALUES
                        {{string.Join(",\n            ", toolValues)}}
                    ) AS t(server, tool, macro_name, description, example)
                );
                """);
        }

        sb.AppendLine();

        // Generate mcp_tool_params() macro with parameter details
        var paramValues = new List<string>();
        foreach (var tool in tools)
        {
            var macroName = SanitizeName($"{tool.ServerName}_{tool.ToolName}");
            var parameters = ExtractParameters(tool.InputSchema);

            foreach (var param in parameters)
            {
                var paramDesc = EscapeSql(param.Description ?? "");
                var defaultVal = param.Default?.ToString() ?? "";
                paramValues.Add($"('{EscapeSql(tool.ServerName)}', '{macroName}', '{param.Name}', '{param.OriginalName}', '{param.Type}', {(param.Required ? "true" : "false")}, '{paramDesc}', '{EscapeSql(defaultVal)}')");
            }
        }

        if (paramValues.Count == 0)
        {
            sb.AppendLine("""
                CREATE OR REPLACE MACRO mcp_tool_params() AS TABLE (
                    SELECT NULL::VARCHAR AS server, NULL::VARCHAR AS macro_name, NULL::VARCHAR AS param_name,
                           NULL::VARCHAR AS original_name, NULL::VARCHAR AS param_type, NULL::BOOLEAN AS required,
                           NULL::VARCHAR AS description, NULL::VARCHAR AS default_value
                    WHERE false
                );
                """);
        }
        else
        {
            sb.AppendLine($$"""
                CREATE OR REPLACE MACRO mcp_tool_params() AS TABLE (
                    SELECT * FROM (VALUES
                        {{string.Join(",\n            ", paramValues)}}
                    ) AS t(server, macro_name, param_name, original_name, param_type, required, description, default_value)
                );
                """);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds an example usage string for a tool macro.
    /// </summary>
    private static string BuildExampleUsage(string macroName, IReadOnlyList<McpToolParameter> parameters)
    {
        if (parameters.Count == 0)
            return $"SELECT * FROM {macroName}()";

        var requiredParams = parameters.Where(p => p.Required).ToList();
        if (requiredParams.Count == 0)
            return $"SELECT * FROM {macroName}()";

        var paramExamples = requiredParams.Select(p => $"{p.Name} := '...'");
        return $"SELECT * FROM {macroName}({string.Join(", ", paramExamples)})";
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
                OriginalName = prop.Name,  // Preserve original for JSON key
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
            return "'{}'";
        }

        // Build JSON for MCP tool parameters
        // JSON key uses OriginalName (MCP expects exact case), SQL param uses Name (sanitized, quoted for reserved keywords)
        var jsonPairs = parameters.Select(p =>
            $"'{p.OriginalName}', \"{p.Name}\"");

        // _mcp_call_internal has exactly 3 parameters, so the 3rd DuckDB argument maps directly
        // to the UDF's params_json parameter. Pass the JSON object itself, not an envelope.
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
