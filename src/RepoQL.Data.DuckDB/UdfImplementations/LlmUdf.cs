using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for LLM-powered operations on repository data.
/// Provides summarization and extraction capabilities via LLM API calls.
/// </summary>
[UdfClass]
public class LlmUdf(ILlmProvider llmProvider)
{
    /// <summary>
    /// Ask a question about data using LLM.
    /// Internal UDF called by ask() macro.
    /// </summary>
    /// <param name="jsonData">JSON array of result rows from the query.</param>
    /// <param name="intent">What the caller hopes to find/understand.</param>
    /// <param name="maxTokens">Approximate token limit for the response (default 500).</param>
    /// <returns>Text response addressing the caller's intent.</returns>
    [ScalarUdf("_ask_internal", MacroName = "ask", Description = "Ask a question about query results using LLM")]
    public string Ask(
        string jsonData,
        string intent,
        [UdfDefault("500")] int maxTokens)
    {
        if (!llmProvider.Enabled)
            return "LLM not configured (set OPENROUTER_API_KEY)";

        try
        {
            // Block synchronously on async call (required by DuckDB UDF framework)
            var result = llmProvider.SummarizeAsync(jsonData, intent, maxTokens, repoTree: null, ct: CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return result ?? "No response from LLM";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Extract relevant snippets with LLM analysis.
    /// Internal UDF called by llm_extract macro.
    /// Returns markdown report with code blocks and synthesis.
    /// </summary>
    /// <param name="jsonData">JSON array of result rows from the query.</param>
    /// <param name="intent">What the caller is looking for.</param>
    /// <returns>Markdown report with URIs, code snippets, and synthesis.</returns>
    [ScalarUdf("_llm_extract_internal", MacroName = "llm_extract", Description = "LLM-powered extraction of relevant code snippets")]
    public string Extract(
        string jsonData,
        string intent)
    {
        if (!llmProvider.Enabled)
            return "LLM not configured (set OPENROUTER_API_KEY)";

        try
        {
            // TODO: Implement readUri callback once tool calling is fully supported
            // For now, uses simple extraction without tool access
            var readUri = (string uri, int contextLines) =>
                $"[Reading {uri} not yet supported in SQL UDF context]";

            // Block synchronously on async call (required by DuckDB UDF framework)
            var result = llmProvider.ExtractAsync(jsonData, intent, readUri, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return result ?? "No response from LLM";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
