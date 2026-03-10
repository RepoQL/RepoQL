namespace RepoQL.Contracts.Inference;

/// <summary>
/// Purpose: Defines the host-facing abstraction for cloud inference completion flows.
/// Complexity: Covers simple completion, tool-assisted completion, and provider availability.
/// </summary>
public interface IInferenceProvider
{
    /// <summary>
    /// Whether the inference service is configured and reachable enough to be used.
    /// </summary>
    bool Available { get; }

    /// <summary>
    /// Complete a prompt without tool execution.
    /// </summary>
    Task<InferenceResult> CompleteAsync(
        InferenceRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Complete a prompt while allowing the model to execute host-provided tools.
    /// </summary>
    Task<InferenceResult> CompleteWithToolsAsync(
        InferenceRequest request,
        ToolOptions toolOptions,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct = default);
}
