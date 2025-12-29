namespace RepoQL.Data.DuckDB;

/// <summary>
/// Interface for calling external MCP tools from SQL UDFs.
/// </summary>
public interface IMcpToolCaller
{
    /// <summary>
    /// Calls an MCP tool synchronously (blocks until result is available).
    /// </summary>
    /// <param name="serverName">Name of the MCP server to call</param>
    /// <param name="toolName">Name of the tool to invoke</param>
    /// <param name="paramsJson">Optional JSON string containing tool parameters</param>
    /// <returns>JSON string result or error JSON</returns>
    string CallToolSync(string serverName, string toolName, string? paramsJson);
}
