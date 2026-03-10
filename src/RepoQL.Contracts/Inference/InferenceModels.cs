namespace RepoQL.Contracts.Inference;

/// <summary>
/// Purpose: Conveys how much reasoning effort the inference service should apply.
/// Complexity: Small enum mapped to provider-specific model and runtime settings.
/// </summary>
public enum InferenceEffort
{
    Low,
    Balanced,
    High
}

/// <summary>
/// Purpose: Represents a completion request in transport-independent terms.
/// Complexity: Holds prompt, optional context/system text, effort, and output guidance.
/// </summary>
public record InferenceRequest
{
    public required string Prompt { get; init; }
    public string? Context { get; init; }
    public string? System { get; init; }
    public InferenceEffort Effort { get; init; } = InferenceEffort.Balanced;
    public int MaxTokens { get; init; }
}

/// <summary>
/// Purpose: Represents the synthesized completion returned by the inference service.
/// Complexity: Carries answer content plus reasoning, model, and token accounting.
/// </summary>
public record InferenceResult
{
    public required string Content { get; init; }
    public string? Reasoning { get; init; }
    public string? Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int ThinkingTokens { get; init; }
    public int ToolTokens { get; init; }
}

/// <summary>
/// Purpose: Describes the tool budget and definitions for a tool-assisted completion.
/// Complexity: Bundles the allowed tools with round and token budget controls.
/// </summary>
public record ToolOptions
{
    public required IReadOnlyList<InferenceToolDefinition> Tools { get; init; }
    public int ToolTokenBudget { get; init; } = 30_000;
    public int MaxRounds { get; init; } = 5;
}

/// <summary>
/// Purpose: Describes a tool exposed to the inference service.
/// Complexity: Tool metadata is limited to name, human description, and JSON schema.
/// </summary>
public record InferenceToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string ParametersJson { get; init; }
}

/// <summary>
/// Purpose: Represents a tool invocation requested by the inference service.
/// Complexity: Carries the stable call id, tool name, and JSON-encoded arguments.
/// </summary>
public record ToolCall
{
    public required string CallId { get; init; }
    public required string Tool { get; init; }
    public required string ArgumentsJson { get; init; }
}

/// <summary>
/// Purpose: Represents the host's response after executing a requested tool.
/// Complexity: Includes result content, error state, and token usage accounting.
/// </summary>
public record ToolCallResult
{
    public required string Content { get; init; }
    public bool IsError { get; init; }
    public int TokensUsed { get; init; }
}
