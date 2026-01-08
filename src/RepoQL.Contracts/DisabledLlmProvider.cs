namespace RepoQL.Contracts;

public sealed class DisabledLlmProvider : ILlmProvider
{
    public bool Enabled => false;
    public string Model => "disabled";

    public Task<string> SummarizeAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
        => Task.FromResult("LLM not configured (set OPENROUTER_API_KEY)");

    public Task<LlmSummaryResult> SummarizeWithReasoningAsync(string jsonData, string intent, int maxTokens = 500, string? repoTree = null, CancellationToken ct = default)
        => Task.FromResult(new LlmSummaryResult("LLM not configured (set OPENROUTER_API_KEY)"));

    public Task<string> ExtractAsync(string jsonData, string intent, Func<string, int, string> readUri, CancellationToken ct = default)
        => Task.FromResult("LLM not configured (set OPENROUTER_API_KEY)");

    public Task<string> ExtractKeywordsAsync(string question, CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
