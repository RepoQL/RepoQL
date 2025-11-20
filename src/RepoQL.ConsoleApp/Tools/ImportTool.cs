using System.ComponentModel;
using System.Linq;
using ModelContextProtocol.Server;
using RepoQL.ConsoleApp.Helpers;
using RepoQL.Contracts;
using RepoQL.Protocol;

namespace RepoQL.ConsoleApp.Tools;

[McpServerToolType]
internal sealed class ImportTool(RepoQlClientProvider clientProvider)
{
    private readonly RepoQlClientProvider _clientProvider = clientProvider ?? throw new ArgumentNullException(nameof(clientProvider));

    private const string ImportInstructions =
        """
        Import an external data source (e.g., a GitHub repository) into the current repoql datastore so that it can be queried alongside existing data.

        Provide a URI supported by importers such as `github://owner/repo@ref`.
        Optionally specify which pipeline stage to wait for [Discovery|Indexing|SemanticIndexing|Analysis|Unspecified]. Defaults to Indexing. Use Unspecified to return immediately.
        """;

    [McpServerTool(ReadOnly = false, Destructive = false, OpenWorld = false, Name = "import"), Description(ImportInstructions)]
    public async Task<string> ImportAsync(
        [Description("URI to import (e.g., github://owner/repo@ref).")] string uri,
        [Description("Pipeline stage to wait for before returning. Defaults to Indexing; pass Unspecified to avoid waiting.")] string waitFor = "Indexing",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("uri is required", nameof(uri));

        var parsedStage = ParseWaitStage(waitFor);
        PipelineStage? stageFilter = parsedStage;

        try
        {
            var client = await _clientProvider.GetClientAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.ImportRepositoryAsync(uri.Trim(), stageFilter, cancellationToken).ConfigureAwait(false);

            var stageSummary = string.Join(", ",
                status.Stages.Select(s => $"{s.Stage}: busy={s.Busy} queued={s.Queued} inProgress={s.InProgress}"));

            // Extract repository information for query guidance
            var uriPattern = GetUriPattern(uri.Trim());

            return $"""
                Import completed: {uri.Trim()}

                Pipeline status: reindexing={status.Reindexing}, writerPending={status.WriterPending}
                Stages: {stageSummary}

                To query the imported content, use:
                - File search: SELECT uri, score FROM file_search('keywords', question := 'your question', k := 10) WHERE uri LIKE '{uriPattern}%'
                - Xray scan: Use pattern="{uriPattern}/**/*" with the xray tool
                - Document list: SELECT document_uri, file_name, headline FROM xray_documents() WHERE document_uri LIKE '{uriPattern}%'

                Example: To see what was imported, run:
                SELECT COUNT(*) as files FROM xray_documents() WHERE document_uri LIKE '{uriPattern}%'

                Note: Re-importing the same repository will perform an incremental update - only new or changed files will be reprocessed.
                """;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(ex.ToString());
            return $"Import failed: {ex.Message}";
        }
    }

    private static PipelineStage ParseWaitStage(string waitFor)
    {
        if (string.IsNullOrWhiteSpace(waitFor))
            return PipelineStage.Indexing;

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
}
