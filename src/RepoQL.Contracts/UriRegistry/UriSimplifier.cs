namespace RepoQL.Contracts;

/// <summary>
/// Simplifies line ranges back to canonical URIs (file, symbol, or line range).
///
/// Purpose: Convert line-range results from set operations back to the most
/// intuitive URI form. Whole files become file URIs. Exact symbol matches
/// become symbol URIs. Partial matches become line range URIs.
///
/// Complexity: Symbol lookup is O(n) where n = symbols in file. Lookup is
/// done lazily per result, acceptable for typical result sets.
/// </summary>
public static class UriSimplifier
{
    /// <summary>
    /// Simplifies a line range result to the most canonical URI form.
    /// </summary>
    /// <param name="fileUri">The file URI (without fragment).</param>
    /// <param name="range">The line range result.</param>
    /// <param name="entry">The file entry containing symbols and line count.</param>
    /// <returns>Canonical URI: file URI if whole file, symbol URI if exact match, else line range URI.</returns>
    public static RepoUri Simplify(RepoUri fileUri, LineRange range, FileEntry entry)
    {
        // Invalid range returns file URI as fallback
        if (!range.IsValid)
            return fileUri;

        // Check if range is whole file
        if (entry.LineCount > 0 && range.Start == 1 && range.End == entry.LineCount)
            return fileUri;

        // Check if range exactly matches a symbol
        foreach (var (symbolUri, symbolEntry) in entry.Symbols)
        {
            if (symbolEntry.HasSpan &&
                symbolEntry.StartLine == range.Start &&
                symbolEntry.EndLine == range.End)
            {
                return symbolUri;
            }
        }

        // Return line range URI
        return fileUri.WithLineRange(range.Start, range.End);
    }
}

/// <summary>
/// Extension methods for RepoUri line range handling.
/// </summary>
public static class RepoUriLineRangeExtensions
{
    /// <summary>
    /// Creates a new URI with a line range fragment.
    /// </summary>
    /// <param name="uri">The base URI (without fragment).</param>
    /// <param name="startLine">Start line (1-based, inclusive).</param>
    /// <param name="endLine">End line (1-based, inclusive).</param>
    /// <returns>URI with #line=start,end fragment.</returns>
    public static RepoUri WithLineRange(this RepoUri uri, int startLine, int endLine)
    {
        var baseUri = uri.Container.AbsoluteUri;
        return RepoUri.Parse($"{baseUri}#line={startLine},{endLine}");
    }
}
