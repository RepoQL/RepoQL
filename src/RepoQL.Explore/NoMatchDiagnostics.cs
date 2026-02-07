namespace RepoQL.Explore;

/// <summary>
/// Purpose: Produces actionable error messages when a read pattern matches nothing.
/// Complexity: Parses URI fragments to distinguish file-not-found from symbol-not-found,
/// checks index status for pending files, and suggests recovery actions.
/// </summary>
public static class NoMatchDiagnostics
{
    public static async Task<string> DiagnoseAsync(
        string uriPattern,
        IReadContentProvider contentProvider,
        IndexerStatus status,
        CancellationToken ct)
    {
        // Multi-pattern URIs (semicolon-separated) — keep generic
        if (uriPattern.Contains(';'))
            return FormatGenericMessage(uriPattern, status);

        // Check for fragment
        var hashIndex = uriPattern.IndexOf('#');
        if (hashIndex > 0)
        {
            var baseUri = uriPattern[..hashIndex];
            var fragment = uriPattern[(hashIndex + 1)..];

            // Check if the base file exists
            var baseResults = await contentProvider.FetchGlobAsync(baseUri, ct).ConfigureAwait(false);
            if (baseResults.Count > 0)
            {
                // File exists but fragment didn't match
                if (fragment.StartsWith("symbol=", StringComparison.OrdinalIgnoreCase))
                {
                    var symbolPattern = fragment["symbol=".Length..];
                    return $"File exists but no symbols matched '{symbolPattern}' in {baseUri}.\n" +
                           $"Try: read(\"{baseUri}#symbol=*\", 2000) to see all symbols, " +
                           $"or read(\"{baseUri} => structure\", 1500) to see signatures.";
                }

                if (fragment.StartsWith("line=", StringComparison.OrdinalIgnoreCase))
                {
                    return $"File exists but line range '{fragment}' is out of bounds for {baseUri}.\n" +
                           $"Try: read(\"{baseUri}\", 2000) to see the full file.";
                }

                return $"File exists but fragment '#{fragment}' didn't match in {baseUri}.\n" +
                       $"Try: read(\"{baseUri}\", 2000) to see the full file.";
            }
        }

        // No fragment, or base file also not found
        var isGlob = uriPattern.Contains('*') || uriPattern.Contains('?');

        if (status.IndexPending > 0)
        {
            var target = isGlob ? $"pattern: {uriPattern}" : uriPattern;
            return $"No files matched {target}.\n" +
                   $"{status.IndexPending} files pending indexing — the target may not be indexed yet.\n" +
                   "Try again shortly, or read(\"file:///** => tree: folders\", 1500) to see what's indexed.";
        }

        if (isGlob)
        {
            return $"No files matched pattern: {uriPattern}\n" +
                   "Try: read(\"file:///** => tree: folders\", 1500) to see available paths, " +
                   "or explore(intent=\"Locate\", keywords=\"...\", tokenBudget=1500) to search.";
        }

        return $"File not found: {uriPattern}\n" +
               "Try: read(\"file:///** => tree: folders\", 1500) to see available files.";
    }

    private static string FormatGenericMessage(string uriPattern, IndexerStatus status)
    {
        var msg = $"No files matched: {uriPattern}";
        if (status.IndexPending > 0)
            msg += $"\n{status.IndexPending} files pending indexing.";
        msg += "\nTry: read(\"file:///** => tree: folders\", 1500) to see available paths.";
        return msg;
    }
}
