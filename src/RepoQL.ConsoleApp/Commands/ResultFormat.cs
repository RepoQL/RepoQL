namespace RepoQL.ConsoleApp.Commands;

/// <summary>
///   The format that the results will be returned in - unstructured is adaptive and tries to present the data the best way possible, but is not predictable
///   JsonLD is great for piping on the command line
/// </summary>
public enum ResultFormat
{
    /// <summary>
    ///    Token-Oriented Object Notation - compact, LLM-friendly tabular format
    ///    with ~40% fewer tokens than JSON. Default for MCP tools.
    /// </summary>
    Toon,
    /// <summary>
    ///    Great for piping - json elements, one per line
    /// </summary>
    JsonLine
}