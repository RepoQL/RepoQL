using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF for parsing structured data from text into JSON.
/// Detects and converts: JSON, JSONL, TSV, CSV, YAML, and embedded structured data.
/// </summary>
/// <remarks>
/// <para><b>Purpose:</b> Provide SQL-accessible structured data parsing for any text input.
/// Used by MCP macros and available for direct SQL use.</para>
/// <para><b>Complexity:</b> Thin wrapper around StructuredDataExtractor which handles
/// all format detection and parsing logic.</para>
/// </remarks>
[UdfClass]
public class ParseStructuredUdf
{
    /// <summary>
    /// Canonical function name for converting structured text into normalized JSON.
    /// Automatically detects format: JSON, JSONL, TSV, CSV, YAML, or embedded data.
    /// </summary>
    /// <remarks>
    /// Detection priority (optimized for low false positives):
    /// 1. Pure JSON - starts with [ or {
    /// 2. JSONL - multiple lines, each a JSON object
    /// 3. TSV - tab-delimited, 2+ columns, 2+ rows
    /// 4. CSV - comma-delimited, 2+ columns, 2+ rows
    /// 5. YAML - starts with --- or has 2+ key: value lines
    /// 6. Embedded - JSON/YAML/CSV blocks in prose
    /// 7. Structured text - "- Key: Value" with delimiters
    /// 8. Fallback - wraps as {"text": "..."}
    ///
    /// Usage:
    ///   SELECT convert_to_json('id,name\n1,Alice\n2,Bob', 'true')
    ///   SELECT * FROM (SELECT unnest(from_json(convert_to_json(response, 'true'), '["json"]')) AS row)
    /// </remarks>
    [ScalarUdf("convert_to_json", Description = "Canonical parser: detect JSON/JSONL/CSV/TSV/YAML/embedded and return normalized JSON. Unwraps envelope objects by default.")]
    public string ConvertToJson(string? text, [UdfDefault("'true'")] string? unwrap)
    {
        return ParseCore(text, unwrap);
    }

    private static string ParseCore(string? text, string? unwrap)
    {
        var shouldUnwrap = unwrap?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ?? true;
        return StructuredDataExtractor.Extract(text, shouldUnwrap);
    }
}
