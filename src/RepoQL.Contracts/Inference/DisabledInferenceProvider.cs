namespace RepoQL.Contracts.Inference;

/// <summary>
/// Purpose: Provides graceful degradation when the inference service is not configured.
/// Complexity: Returns fixed explanatory results and never attempts remote execution.
/// </summary>
public sealed class DisabledInferenceProvider : IInferenceProvider
{
    private const string DisabledMessage = "Inference service not configured (set inference.service_url and inference.api_key)";

    public bool Available => false;

    public Task<InferenceResult> CompleteAsync(
        InferenceRequest request,
        CancellationToken ct = default)
        => Task.FromResult(new InferenceResult
        {
            Content = DisabledMessage
        });

    public Task<InferenceResult> CompleteWithToolsAsync(
        InferenceRequest request,
        ToolOptions toolOptions,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct = default)
        => CompleteAsync(request, ct);
}
