namespace RepoQL.Web.Services;

/// <summary>
/// Service for testing the read tool with URIs, fragments, modifiers, and budgets.
/// Shows progressive disclosure by revealing how budget affects detail level.
///
/// <para><b>Purpose:</b> Enable developers to test read commands and understand
/// what agents receive at different token budgets.</para>
///
/// <para><b>Complexity:</b> Parses modifier syntax from URI, calls gRPC Read,
/// returns metadata about representation level for educational display.</para>
/// </summary>
internal sealed class ReadService
{
    private readonly RepoQlConnectionManager _connectionManager;
    private readonly ILogger<ReadService> _logger;

    public ReadService(RepoQlConnectionManager connectionManager, ILogger<ReadService> logger)
    {
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Execute a read command and return results with metadata.
    /// </summary>
    public async Task<ReadResult> ReadAsync(ReadParams @params, CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Build the full URI with modifier if specified
        var fullUri = BuildFullUri(@params.Uri, @params.Modifier);

        try
        {
            var client = await _connectionManager.GetClientAsync(ct).ConfigureAwait(false);

            _logger.LogDebug("Executing read (uri={Uri}, budget={Budget})", fullUri, @params.TokenBudget);

            var response = await client.ReadAsync(fullUri, @params.TokenBudget, ct).ConfigureAwait(false);

            stopwatch.Stop();

            if (!response.Success)
            {
                return new ReadResult(
                    Content: null,
                    TokensUsed: 0,
                    DetailLevel: null,
                    FilesRead: 0,
                    FilesOmitted: 0,
                    Duration: stopwatch.Elapsed,
                    Error: response.Error);
            }

            // Estimate tokens used from output length (rough approximation)
            var tokensUsed = EstimateTokens(response.RenderedOutput);

            return new ReadResult(
                Content: response.RenderedOutput,
                TokensUsed: tokensUsed,
                DetailLevel: MapRepresentation(response.Representation),
                FilesRead: response.FilesRead,
                FilesOmitted: response.FilesOmitted,
                Duration: stopwatch.Elapsed,
                Error: null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogDebug(ex, "Read failed for {Uri}", fullUri);

            return new ReadResult(
                Content: null,
                TokensUsed: 0,
                DetailLevel: null,
                FilesRead: 0,
                FilesOmitted: 0,
                Duration: stopwatch.Elapsed,
                Error: ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Predict what detail level would be used for a given budget without executing.
    /// </summary>
    public static string PredictDetailLevel(int budget)
    {
        // Based on typical thresholds from read tool documentation
        return budget switch
        {
            < 100 => "headline",
            < 1000 => "structure",
            _ => "full (for small files)"
        };
    }

    private static string BuildFullUri(string uri, string? modifier)
    {
        if (string.IsNullOrWhiteSpace(modifier) || modifier == "none")
            return uri;

        // Append modifier using => syntax
        // e.g., "file:///src/foo.cs" + "tree" => "file:///src/foo.cs => tree"
        return $"{uri} => {modifier}";
    }

    private static string MapRepresentation(string? representation)
    {
        if (string.IsNullOrEmpty(representation))
            return "unknown";

        // Normalize representation names for display
        if (string.Equals(representation, "full", StringComparison.OrdinalIgnoreCase))
            return "full";
        if (string.Equals(representation, "structure", StringComparison.OrdinalIgnoreCase))
            return "structure";
        if (string.Equals(representation, "headline", StringComparison.OrdinalIgnoreCase))
            return "headline";
        if (string.Equals(representation, "glob", StringComparison.OrdinalIgnoreCase))
            return "tree";
        if (string.Equals(representation, "question", StringComparison.OrdinalIgnoreCase))
            return "answer";

        return representation;
    }

    private static int EstimateTokens(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        // Rough approximation: ~4 characters per token for English text/code
        return content.Length / 4;
    }
}

/// <summary>Parameters for a read operation.</summary>
internal sealed record ReadParams(
    string Uri,
    int TokenBudget,
    string? Modifier = null);

/// <summary>Result of a read operation with detail level metadata.</summary>
internal sealed record ReadResult(
    string? Content,
    int TokensUsed,
    string? DetailLevel,
    int FilesRead,
    int FilesOmitted,
    TimeSpan Duration,
    string? Error);
