namespace RepoQL.ConsoleApp.Commands;

/// <summary>
///   The format that the results will be returned in - unstructured is adaptive and tries to present the data the best way possible, but is not predictable
///   JsonLD is great for piping on the command line
/// </summary>
public enum ResultFormat
{
    /// <summary>
    ///  adaptive and tries to present the data the best way possible, but is not predictable in its format
    /// </summary>
    Unstructured,
    /// <summary>
    ///    Great for piping - json elements, one per line
    /// </summary>
    JsonLD
}