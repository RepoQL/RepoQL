namespace RepoQL.Cloud.Service.Inference;

internal interface IGrokClient
{
    Task<GrokCompletionResult> CompleteAsync(GrokCompletionRequest request, CancellationToken cancellationToken);
}

internal sealed record GrokCompletionRequest(
    IReadOnlyList<GrokMessage> Messages,
    Effort Effort,
    int? MaxTokens,
    IReadOnlyList<ToolDefinition> Tools,
    GrokToolMode ToolMode = GrokToolMode.Auto,
    bool ParallelToolCalls = true,
    bool IncludeEncryptedReasoningContent = false,
    int RoundNumber = 0);

internal sealed record GrokMessage(
    GrokMessageRole Role,
    string? Content,
    IReadOnlyList<GrokFunctionCall>? FunctionCalls = null,
    string? ToolCallId = null,
    string? Reasoning = null,
    string? EncryptedContent = null);

internal sealed record GrokFunctionCall(
    string Id,
    string Name,
    string ArgumentsJson);

internal enum GrokMessageRole
{
    User,
    Assistant,
    Developer,
    Tool
}

internal enum GrokToolMode
{
    Auto,
    Required,
    None
}

internal sealed record GrokCompletionResult(
    string Content,
    string Reasoning,
    StopReason StopReason,
    Usage Usage,
    string Model,
    IReadOnlyList<GrokFunctionCall> ToolCalls,
    string? EncryptedContent);
