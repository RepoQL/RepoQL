using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Commands;
using RepoQL.ConsoleApp.Diagnostics;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class ImportTool(RepoQlClientProvider clientProvider, SelfTestRunner selfTestRunner, QueryExecutor queryExecutor)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));
    private readonly SelfTestRunner _selfTestRunner = selfTestRunner ?? throw new ArgumentNullException(nameof(selfTestRunner));
    private readonly QueryExecutor _queryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));

    private const string ImportInstructions =
        """
        Import or remove an external data source (e.g., a GitHub repository) from the current repoql datastore.

        To import: Provide a URI such as `github://owner/repo@ref`.
        To remove: Prefix the URI with `-` (e.g., `-github://owner/repo`) to delete the import and all its indexed data.

        Import waits for all files to be indexed and have structure embeddings ready for semantic search.

        To see all imports: `SELECT * FROM Filesystems`
        """;

    [McpServerTool(Name = "import", Title = "Import Repository", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = false), Description(ImportInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<CallToolResult> ImportAsync(
        [Description("URI to import (e.g., github://owner/repo@ref). Prefix with '-' to remove an import.")] string uri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return ToolResult.Error("uri is required");

        // Check for removal prefix - server handles this
        var isRemoval = uri.TrimStart().StartsWith('-');

        if (!isRemoval && TrySetWorkingDirectoryFromPrimaryUri(uri))
        {
            // Repo root provided explicitly; proceed to connect and import.
        }

        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.ImportRepositoryAsync(uri.Trim(), cancellationToken).ConfigureAwait(false);

            if (isRemoval)
            {
                return ToolResult.Success($"""
                    Import removed: {uri.Trim().TrimStart('-')}

                    The import and all its indexed data have been deleted.

                    To see remaining imports: SELECT * FROM file_system_mount
                    """);
            }

            // Extract repository information for query guidance
            var uriPattern = GetUriPattern(uri.Trim());

            // Generate tree visualization of imported content
            var treeOutput = await GenerateTreeAsync(uriPattern, cancellationToken).ConfigureAwait(false);

            // Look for repo context file (claude.md > agents.md > readme.md)
            var (contextUri, repoContext) = await TryGetRepoContextAsync(uriPattern, cancellationToken).ConfigureAwait(false);
            var repoContextSection = repoContext != null ? $"\n\n---\n\nSource: {contextUri}\n\n{repoContext}" : "";

            // Build progress summary
            var progressSummary = result.HasOperationProgress
                ? $"Progress: {result.EmbeddedCount}/{result.TotalFiles} files ready"
                  + (result.HasFailures ? $" ({result.FailedCount} failed)" : "")
                : "";

            // Build failure warning if any
            var failureWarning = result.HasFailures
                ? $"\n\nWARNING: {result.FailedCount} file(s) failed to index. Check logs for details."
                : "";

            return ToolResult.Success($"""
                Import completed: {uri.Trim()}
                {progressSummary}

                {treeOutput}

                To query the imported content, use:
                - File search: SELECT uri, score FROM search('keywords', scope := '{uriPattern}%', k := 10)
                - Explore scan: Use uriGlob="{uriPattern}/**" with the explore tool
                - Document list: SELECT uri, headline FROM Files WHERE uri LIKE '{uriPattern}%'

                Note: Re-importing the same repository will perform an incremental update.{failureWarning}{repoContextSection}
                """);
        }
        catch (Exception ex)
        {
            var cleanMessage = ErrorClassifier.GetCleanMessage(ex);
            await Console.Error.WriteLineAsync(cleanMessage);

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(DiagnosticCollectionMode.Fast, cancellationToken);
                return ToolResult.Error($"Import failed: {cleanMessage}\n\n{diagnostics}");
            }

            return ToolResult.Error($"Import failed: {cleanMessage}");
        }
    }

    private static string GetUriPattern(string importUri)
    {
        // Extract the base URI pattern for querying
        // Examples:
        //   github://owner/repo -> github://owner/repo
        //   github://owner/repo@branch -> github://owner/repo
        //   https://github.com/owner/repo -> github://owner/repo

        var uri = importUri.Trim();

        // Handle github:// scheme
        if (uri.StartsWith("github://", StringComparison.OrdinalIgnoreCase))
        {
            // Remove @branch if present
            var atIndex = uri.IndexOf('@');
            if (atIndex > 0)
                return uri.Substring(0, atIndex);
            return uri;
        }

        // Handle https://github.com/ URLs
        if (uri.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.Substring("https://github.com/".Length);
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var owner = parts[0];
                var repo = parts[1].Split('@')[0]; // Remove branch if present
                return $"github://{owner}/{repo}";
            }
        }

        // Default: return as-is
        return uri;
    }

    private bool TrySetWorkingDirectoryFromPrimaryUri(string uri)
    {
        const string Prefix = "primary://";
        if (!uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathPart = uri.Substring(Prefix.Length).Trim();
        pathPart = pathPart.TrimStart('/'); // tolerate primary:///C:/repo
        if (string.IsNullOrWhiteSpace(pathPart))
        {
            throw new ArgumentException("primary:// URI must include a filesystem path", nameof(uri));
        }

        var fullPath = Path.GetFullPath(pathPart);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The path '{fullPath}' does not exist. Provide a valid repository root path.");
        }

        _clientProvider.SetWorkingDirectory(fullPath);
        return true;
    }

    /// <summary>
    /// Generate a tree visualization of the imported content.
    /// </summary>
    private async Task<string> GenerateTreeAsync(string uriPattern, CancellationToken cancellationToken)
    {
        try
        {
            var escapedPattern = uriPattern.Replace("'", "''");
            var sql = $"""
                SELECT tree(
                    json_group_array(uri ORDER BY uri),
                    json_group_array(headline ORDER BY uri),
                    false
                )
                FROM Files
                WHERE uri LIKE '{escapedPattern}%'
                """;
            var result = await _queryExecutor.ExecuteAsync(sql, 1, ResultFormat.Toon, cancellationToken: cancellationToken).ConfigureAwait(false);

            var tree = string.Join(Environment.NewLine, result.Lines);
            if (string.IsNullOrWhiteSpace(tree))
            {
                return $"(No files indexed yet for {uriPattern})";
            }

            var fileCount = result.TotalRowCount > 0 ? result.TotalRowCount : CountFiles(tree);
            return $"Imported {fileCount} files:\n{tree}";
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[ImportTool] GenerateTreeAsync failed: {ex.GetType().Name}: {ex.Message}");
            return $"(Tree generation pending - files are being indexed)";
        }
    }

    /// <summary>
    /// Find and return the content of the first repo context file (claude.md > agents.md > readme.md).
    /// </summary>
    private async Task<(string? Uri, string? Content)> TryGetRepoContextAsync(string uriPattern, CancellationToken cancellationToken)
    {
        try
        {
            var escaped = uriPattern.Replace("'", "''");

            // Find which context file exists (by precedence)
            var findSql = $"""
                SELECT doc.uri
                FROM node doc
                JOIN artifact art ON art.id = doc.artifact_id
                WHERE doc.kind = 'document'
                  AND art.text_content IS NOT NULL
                  AND lower(doc.uri) IN (
                    lower('{escaped}/claude.md'),
                    lower('{escaped}/agents.md'),
                    lower('{escaped}/readme.md')
                  )
                ORDER BY CASE
                    WHEN lower(doc.uri) = lower('{escaped}/claude.md') THEN 1
                    WHEN lower(doc.uri) = lower('{escaped}/agents.md') THEN 2
                    WHEN lower(doc.uri) = lower('{escaped}/readme.md') THEN 3
                END
                LIMIT 1
                """;
            var findResult = await _queryExecutor.ExecuteAsync(findSql, 1, ResultFormat.Toon, cancellationToken: cancellationToken).ConfigureAwait(false);
            var uri = string.Join("", findResult.Lines).Trim();
            if (string.IsNullOrWhiteSpace(uri) || uri == "null")
                return (null, null);

            // Read its content with a token budget
            var readSql = $"""
                SELECT art.text_content
                FROM node doc
                JOIN artifact art ON art.id = doc.artifact_id
                WHERE doc.uri = '{uri.Replace("'", "''")}'
                LIMIT 1
                """;
            var readResult = await _queryExecutor.ExecuteAsync(readSql, 1, ResultFormat.Toon, tokenBudget: 4000, cancellationToken: cancellationToken).ConfigureAwait(false);
            var content = string.Join(Environment.NewLine, readResult.Lines);
            if (string.IsNullOrWhiteSpace(content) || content == "null")
                return (null, null);

            return (uri, content);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[ImportTool] TryGetRepoContextAsync failed: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Count files in tree output by counting lines that don't end with /
    /// </summary>
    private static long CountFiles(string tree)
    {
        return tree.Split('\n').Count(line =>
        {
            var trimmed = line.TrimEnd();
            return !string.IsNullOrEmpty(trimmed) &&
                   !trimmed.EndsWith('/') &&
                   !trimmed.EndsWith("://") &&
                   !trimmed.Contains("files)");
        });
    }
}
