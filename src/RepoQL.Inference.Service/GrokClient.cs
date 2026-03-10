using System.Diagnostics;
using Grpc.Core;
using Microsoft.Extensions.Options;
using XaiApi;

namespace RepoQL.Inference.Service;

/// <summary>
/// Purpose: Translate RepoQL inference conversations into xAI gRPC chat calls.
/// Complexity: Maps multi-turn message history, tool configuration, encrypted reasoning state, and provider failures.
/// </summary>
internal sealed class GrokClient : IGrokClient
{
    private static readonly ActivitySource ActivitySource = new("RepoQL.Inference.Grok");

    private readonly IXaiChatClient _chatClient;
    private readonly InferenceServiceOptions _options;
    private readonly ILogger<GrokClient> _logger;

    public GrokClient(
        IXaiChatClient chatClient,
        IOptions<InferenceServiceOptions> options,
        ILogger<GrokClient> logger)
    {
        _chatClient = chatClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GrokCompletionResult> CompleteAsync(GrokCompletionRequest request, CancellationToken cancellationToken)
    {
        var settings = EffortModelSelector.Resolve(request.Effort, _options);
        using var activity = ActivitySource.StartActivity("grok.complete");
        activity?.SetTag("repoql.inference.effort", settings.EffectiveEffort.ToString());
        activity?.SetTag("repoql.inference.model", settings.Model);
        activity?.SetTag("repoql.inference.message_count", request.Messages.Count);
        activity?.SetTag("repoql.inference.tool_mode", request.ToolMode.ToString());
        activity?.SetTag("repoql.inference.tool_count", request.Tools.Count);
        activity?.SetTag("repoql.inference.round", request.RoundNumber);

        var grpcRequest = new GetCompletionsRequest
        {
            Model = settings.Model,
            Temperature = (float)settings.Temperature,
            ParallelToolCalls = request.ParallelToolCalls
        };

        if (request.MaxTokens is > 0)
            grpcRequest.MaxTokens = request.MaxTokens.Value;

        foreach (var message in request.Messages)
            grpcRequest.Messages.Add(CreateMessage(message));

        foreach (var tool in request.Tools)
        {
            grpcRequest.Tools.Add(new Tool
            {
                Function = new Function
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.ParametersJson
                }
            });
        }

        if (request.Tools.Count > 0 || request.ToolMode == GrokToolMode.None)
        {
            grpcRequest.ToolChoice = new ToolChoice
            {
                Mode = MapToolMode(request.ToolMode)
            };
        }

        if (request.IncludeEncryptedReasoningContent)
            grpcRequest.UseEncryptedContent = true;

        var headers = new Metadata
        {
            { "authorization", $"Bearer {_options.GrokApiKey}" }
        };

        try
        {
            _logger.LogInformation(
                "Calling Grok model {Model} with {MessageCount} messages and tool mode {ToolMode}",
                settings.Model,
                request.Messages.Count,
                request.ToolMode);

            var response = await _chatClient.GetCompletionAsync(
                grpcRequest,
                headers,
                DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            var choice = response.Outputs.FirstOrDefault()
                ?? throw new GrokApiException("Grok returned no completion choices", StatusCode.Internal);

            var content = choice.Message?.Content ?? "";
            var reasoning = choice.Message?.ReasoningContent ?? "";
            var toolCalls = choice.Message?.ToolCalls.Select(static call => new GrokFunctionCall(
                call.Id,
                call.Function?.Name ?? "",
                call.Function?.Arguments ?? "")).ToArray() ?? [];
            var usage = response.Usage is null
                ? new Usage()
                : new Usage
                {
                    InputTokens = response.Usage.PromptTokens,
                    OutputTokens = response.Usage.CompletionTokens,
                    ThinkingTokens = response.Usage.ReasoningTokens
                };

            activity?.SetTag("repoql.inference.input_tokens", usage.InputTokens);
            activity?.SetTag("repoql.inference.output_tokens", usage.OutputTokens);
            activity?.SetTag("repoql.inference.thinking_tokens", usage.ThinkingTokens);
            activity?.SetTag("repoql.inference.tool_call_count", toolCalls.Length);

            return new GrokCompletionResult(
                content,
                reasoning,
                MapStopReason(choice.FinishReason),
                usage,
                settings.Model,
                toolCalls,
                choice.Message?.EncryptedContent);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GrokTimeoutException("Grok request timed out");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
        {
            throw new GrokTimeoutException("Grok request timed out", ex);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            throw new GrokTimeoutException("Grok service unavailable", ex);
        }
        catch (RpcException ex)
        {
            throw new GrokApiException($"Grok request failed: {ex.Status.Detail}", ex.StatusCode, ex);
        }
    }

    private static Message CreateMessage(GrokMessage source)
    {
        var message = new Message { Role = MapMessageRole(source.Role) };

        if (!string.IsNullOrWhiteSpace(source.Content))
            message.Content.Add(new Content { Text = source.Content });

        if (!string.IsNullOrWhiteSpace(source.Reasoning))
            message.ReasoningContent = source.Reasoning;

        if (!string.IsNullOrWhiteSpace(source.EncryptedContent))
            message.EncryptedContent = source.EncryptedContent;

        if (!string.IsNullOrWhiteSpace(source.ToolCallId))
            message.ToolCallId = source.ToolCallId;

        if (source.FunctionCalls is not null)
        {
            foreach (var functionCall in source.FunctionCalls)
            {
                message.ToolCalls.Add(new ToolCall
                {
                    Id = functionCall.Id,
                    Type = (ToolCallType)1,
                    Function = new FunctionCall
                    {
                        Name = functionCall.Name,
                        Arguments = functionCall.ArgumentsJson
                    }
                });
            }
        }

        return message;
    }

    private static MessageRole MapMessageRole(GrokMessageRole role)
    {
        return role switch
        {
            GrokMessageRole.User => (MessageRole)1,
            GrokMessageRole.Assistant => (MessageRole)2,
            GrokMessageRole.Developer => (MessageRole)6,
            GrokMessageRole.Tool => (MessageRole)5,
            _ => (MessageRole)1
        };
    }

    private static ToolMode MapToolMode(GrokToolMode mode)
    {
        return mode switch
        {
            GrokToolMode.Required => (ToolMode)3,
            GrokToolMode.None => (ToolMode)2,
            _ => (ToolMode)1
        };
    }

    private static StopReason MapStopReason(FinishReason finishReason)
    {
        return finishReason switch
        {
            (FinishReason)1 => StopReason.MaxTokens,
            (FinishReason)4 => StopReason.Stop,
            _ => StopReason.Stop
        };
    }
}

internal interface IXaiChatClient
{
    Task<GetChatCompletionResponse> GetCompletionAsync(
        GetCompletionsRequest request,
        Metadata headers,
        DateTime deadline,
        CancellationToken cancellationToken);
}

internal sealed class XaiChatClientAdapter(Chat.ChatClient client) : IXaiChatClient
{
    public async Task<GetChatCompletionResponse> GetCompletionAsync(
        GetCompletionsRequest request,
        Metadata headers,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        return await client.GetCompletionAsync(
                request,
                headers,
                deadline,
                cancellationToken)
            .ResponseAsync.ConfigureAwait(false);
    }
}
