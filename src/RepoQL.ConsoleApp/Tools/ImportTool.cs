using System.ComponentModel;
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

        Optionally specify which pipeline stage to wait for [Discovery|Indexing|SemanticIndexing|Analysis|Unspecified]. Defaults to SemanticIndexing to ensure embeddings are ready for search. Use Unspecified to return immediately.
        """;

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false, Name = "import"), Description(ImportInstructions)]
    [McpMeta("defer_loading", false)]
    [McpMeta("allowed_callers", JsonValue = """["direct", "code_execution_20250825"]""")]
    public async Task<string> ImportAsync(
        [Description("URI to import (e.g., github://owner/repo@ref). Prefix with '-' to remove an import.")] string uri,
        [Description("Pipeline stage to wait for before returning. Defaults to SemanticIndexing to ensure embeddings are ready; pass Indexing for faster return (hot path only) or Unspecified to return immediately.")] string waitFor = "SemanticIndexing",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("uri is required", nameof(uri));

        // Check for removal prefix - server handles this
        var isRemoval = uri.TrimStart().StartsWith('-');

        if (!isRemoval && TrySetWorkingDirectoryFromPrimaryUri(uri))
        {
            // Repo root provided explicitly; proceed to connect and import.
        }

        var parsedStage = ParseWaitStage(waitFor);
        PipelineStage? stageFilter = parsedStage;

        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.ImportRepositoryAsync(uri.Trim(), stageFilter, cancellationToken).ConfigureAwait(false);

            var stageSummary = string.Join(", ",
                status.Stages.Select(s => $"{s.Stage}: busy={s.Busy} queued={s.Queued} inProgress={s.InProgress}"));

            if (isRemoval)
            {
                return $"""
                    Import removed: {uri.Trim().TrimStart('-')}

                    The import and all its indexed data have been deleted.

                    To see remaining imports: SELECT * FROM file_system_mount
                    """;
            }

            // Extract repository information for query guidance
            var uriPattern = GetUriPattern(uri.Trim());

            // Generate tree visualization of imported content
            var treeOutput = await GenerateTreeAsync(uriPattern, cancellationToken).ConfigureAwait(false);

            return $"""
                Import completed: {uri.Trim()}

                {treeOutput}

                To query the imported content, use:
                - File search: SELECT uri, score FROM search('keywords', scope := '{uriPattern}%', k := 10)
                - Xray scan: Use scope="{uriPattern}/**" with the xray tool
                - Document list: SELECT uri, headline FROM Files WHERE uri LIKE '{uriPattern}%'

                Note: Re-importing the same repository will perform an incremental update.
                """;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);

            // For infrastructure errors, append diagnostic information
            if (ErrorClassifier.IsInfrastructureError(ex))
            {
                var diagnostics = await _selfTestRunner.RunAsync(cancellationToken);
                return $"Import failed: {ex.Message}\n\n{diagnostics}";
            }

            return $"Import failed: {ex.Message}";
        }
    }

    private static PipelineStage ParseWaitStage(string waitFor)
    {
        if (string.IsNullOrWhiteSpace(waitFor))
            return PipelineStage.SemanticIndexing;

        if (Enum.TryParse<PipelineStage>(waitFor.Trim(), ignoreCase: true, out var parsed))
            return parsed;

        throw new ArgumentException($"Invalid pipeline stage '{waitFor}'. Expected one of Discovery, Indexing, SemanticIndexing, Analysis, or Unspecified.", nameof(waitFor));
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
            var sql = $"SELECT tree(list(uri)) FROM Files WHERE uri LIKE '{escapedPattern}%'";
            var result = await _queryExecutor.ExecuteAsync(sql, 1, ResultFormat.Toon, cancellationToken).ConfigureAwait(false);

            var tree = string.Join(Environment.NewLine, result.Lines);
            if (string.IsNullOrWhiteSpace(tree))
            {
                return $"(No files indexed yet for {uriPattern})";
            }

            var fileCount = result.TotalRowCount > 0 ? result.TotalRowCount : CountFiles(tree);
            return $"Imported {fileCount} files:\n{tree}";
        }
        catch
        {
            // If tree generation fails, return a simple message
            return $"(Tree generation pending - files are being indexed)";
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
