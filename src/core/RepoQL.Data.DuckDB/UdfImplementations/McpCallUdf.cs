using System.Text;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for calling external MCP tools from SQL queries.
/// Method signature defines the contract - params beyond index 1 are deserialized from JSON.
/// </summary>
[UdfClass]
public class McpCallUdf(IMcpToolCaller? toolCaller)
{
    /// <summary>
    /// Internal UDF called by generated macros. Returns JSON result or error JSON.
    /// The actual implementation is injected via IMcpToolCaller dependency.
    /// IMPORTANT: params_json must be non-NULL (use '{}' for no params) - DuckDB skips UDF when all args are NULL.
    /// Usage: SELECT _mcp_call_internal('aspire-dashboard', 'list_resources', '{}')
    /// </summary>
    [ScalarUdf("_mcp_call_internal", Description = "Call external MCP tool, returns JSON result", IsPure = false)]
    public string CallTool(
        string server,
        string tool,
        [UdfDefault("'{}'")]string? params_json)
    {
        try
        {
            // Validate required parameters
            if (string.IsNullOrWhiteSpace(server))
                return "{\"error\": \"Server name is required\"}";

            if (string.IsNullOrWhiteSpace(tool))
                return "{\"error\": \"Tool name is required\"}";

            // Check if external tool caller is configured
            if (toolCaller is null)
                return "{\"error\": \"External tool caller not configured\"}";

            // Call the external tool
            var result = toolCaller.CallToolSync(server, tool, params_json);

            // Ensure we never return null - DuckDB would convert to SQL NULL
            return result ?? "{\"error\": \"Callback returned null\"}";
        }
        catch (Exception ex)
        {
            return $"{{\"error\": \"{EscapeJsonString(ex.Message)}\"}}";
        }
    }

    private static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 32)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
