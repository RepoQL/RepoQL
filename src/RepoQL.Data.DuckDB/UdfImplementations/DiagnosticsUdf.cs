using System.Text.Json;
using RepoQL.Contracts.Diagnostics;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for indexing diagnostics and queue inspection.
/// Provides visibility into the indexing pipeline state via SQL.
/// </summary>
[UdfClass]
public class DiagnosticsUdf
{
    // Cached JsonSerializerOptions to avoid allocating on every call (CA1869)
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    /// <summary>
    /// Returns diagnostic information about the indexing pipeline state.
    /// Output format is key-value pairs (survives IL trimming, no JSON required).
    /// </summary>
    /// <remarks>
    /// The dummy parameter exists because DuckDB.NET doesn't reliably support
    /// parameterless UDFs. SQL macros hide this from users.
    /// </remarks>
    [ScalarUdf("_indexing_diagnostics_internal", MacroName = "indexing_diagnostics", Description = "Returns indexing pipeline diagnostics as key-value text", IsPure = false)]
    public string GetDiagnostics([UdfDefault("''")] string? _dummy)
    {
        return IndexingDiagnostics.GetDiagnosticsText();
    }

    /// <summary>
    /// Returns JSON array of queued items in the indexing pipeline.
    /// Can be consumed via: SELECT * FROM (SELECT unnest(indexing_queue()::json[]) as item)
    /// </summary>
    /// <remarks>
    /// The dummy parameter exists because DuckDB.NET doesn't reliably support
    /// parameterless UDFs. SQL macros hide this from users.
    /// </remarks>
    [ScalarUdf("_indexing_queue_internal", MacroName = "indexing_queue", Description = "Returns queued indexing items as JSON array", IsPure = false)]
    public string GetQueue([UdfDefault("''")] string? _dummy)
    {
        var items = IndexingDiagnostics.GetQueuedItems();
        var json = JsonSerializer.Serialize(items, s_jsonOptions);
        return json;
    }
}
