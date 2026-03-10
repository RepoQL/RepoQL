using RepoQL.Contracts;
using RepoQL.Data.DuckDB.UdfFramework;
using RepoQL.Contracts.Inference;

namespace RepoQL.Data.DuckDB.UdfImplementations;

/// <summary>
/// UDF class for LLM-powered operations on repository data.
/// Provides summarization and extraction capabilities via LLM API calls.
/// </summary>
[UdfClass]
public class LlmUdf(IInferenceProvider inferenceProvider)
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
        if (!inferenceProvider.Available)
            return "Inference service not configured (set inference.service_url and inference.api_key)";

        try
        {
            // Block synchronously on async call (required by DuckDB UDF framework)
            var result = inferenceProvider.CompleteAsync(
                    new InferenceRequest
                    {
                        Context = jsonData,
                        Prompt = intent,
                        MaxTokens = maxTokens
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            return result.Content ?? "No response from inference service";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
