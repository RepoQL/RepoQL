using RepoQL.Contracts.Inference;
using RepoQL.Explore;

namespace RepoQL.Explain;

/// <summary>
/// Purpose: Synthesize answers to natural-language questions about a codebase.
/// Complexity: Orchestrates keyword extraction, broad explore search, tree context,
/// and LLM synthesis with tool use — the pure business logic of the explain tool.
/// </summary>
public interface IExplainEngine
{
    /// <summary>
    /// Answer a question by searching the codebase and synthesizing a response.
    /// </summary>
    Task<ExplainResult> ExecuteAsync(
        ExplainRequest request,
        TrustSignal status,
        CancellationToken cancel = default);
}

public sealed class ExplainRequest
{
    public required string Question { get; init; }
    public string? Keywords { get; init; }
    public string? Scope { get; init; }
    public int TokenBudget { get; init; } = 2500;

    /// <summary>Tool definitions the LLM can invoke during synthesis (e.g., read tool).</summary>
    public IReadOnlyList<InferenceToolDefinition>? Tools { get; init; }

    /// <summary>Max tokens per tool call result.</summary>
    public int ToolTokenBudget { get; init; } = 8000;

    /// <summary>Max LLM tool-use rounds.</summary>
    public int MaxRounds { get; init; } = 3;
}

public sealed class ExplainResult
{
    public static ExplainResult Failure(string error) => new() { Success = false, Error = error };

    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RenderedOutput { get; init; }
    public int MatchCount { get; init; }
    public int ContextTokens { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public IReadOnlyList<ExplainToolCall> ToolCalls { get; init; } = [];
    public long ElapsedMs { get; init; }
}

public sealed class ExplainToolCall
{
    public required string Uri { get; init; }
    public int TokensUsed { get; init; }
    public bool IsError { get; init; }
}
