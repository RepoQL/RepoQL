using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf.Collections;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace RepoQL.Cloud.Service.Inference;

/// <summary>
/// Purpose: Expose the inference gRPC contract on top of the Grok client adapter.
/// Complexity: Validates requests, manages the tool loop state machine, enforces budgets and limits, and records telemetry.
/// </summary>
internal sealed class InferenceServiceImpl : InferenceService.InferenceServiceBase
{
    internal const string DefaultSystemPrompt =
        "You are RepoQL's inference service. Answer directly from the supplied context, be precise, and preserve citations when they are present.";

    private static readonly ActivitySource ActivitySource = new("RepoQL.Inference.Service");

    private readonly IGrokClient _grokClient;
    private readonly InferenceServiceOptions _options;
    private readonly ILogger<InferenceServiceImpl> _logger;

    public InferenceServiceImpl(
        IGrokClient grokClient,
        IOptions<InferenceServiceOptions> options,
        ILogger<InferenceServiceImpl> logger)
    {
        _grokClient = grokClient;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task<Completion> Complete(CompleteRequest request, ServerCallContext context)
    {
        ValidateRequest(request);

        using var activity = ActivitySource.StartActivity("inference.complete");
        activity?.SetTag("repoql.inference.effort", request.Effort.ToString());
        activity?.SetTag("repoql.inference.has_context", !string.IsNullOrWhiteSpace(request.Context));
        activity?.SetTag("repoql.inference.max_tokens", request.MaxTokens);

        var systemPrompt = ResolveSystemPrompt(request.System);

        try
        {
            _logger.LogInformation("Complete request received with effort {Effort}", request.Effort);

            var result = await _grokClient.CompleteAsync(
                new GrokCompletionRequest(
                    BuildInitialMessages(systemPrompt, request.Context, request.Prompt),
                    request.Effort,
                    request.MaxTokens > 0 ? request.MaxTokens : null,
                    [],
                    RoundNumber: 0),
                context.CancellationToken).ConfigureAwait(false);

            activity?.SetTag("repoql.inference.model", result.Model);
            activity?.SetTag("repoql.inference.input_tokens", result.Usage.InputTokens);
            activity?.SetTag("repoql.inference.output_tokens", result.Usage.OutputTokens);
            activity?.SetTag("repoql.inference.thinking_tokens", result.Usage.ThinkingTokens);

            _logger.LogInformation("Completion returned from model {Model}", result.Model);

            return CreateCompletion(result, result.StopReason, result.Usage, toolTokens: 0);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Cancelled, "Request cancelled"));
        }
        catch (GrokTimeoutException ex)
        {
            _logger.LogWarning(ex, "Grok request timed out");
            throw new RpcException(new Status(StatusCode.Unavailable, "Grok API timed out — retry shortly"));
        }
        catch (GrokApiException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
        {
            _logger.LogWarning(ex, "Grok rate limited the request");
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Grok API rate limited — retry shortly"));
        }
        catch (GrokApiException ex)
        {
            _logger.LogError(ex, "Grok request failed");
            throw new RpcException(new Status(StatusCode.Internal, "Grok API returned an error"));
        }
    }

    public override async Task CompleteWithTools(
        IAsyncStreamReader<ClientMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        ServerCallContext context)
    {
        using var activity = ActivitySource.StartActivity("inference.complete_with_tools");

        try
        {
            var request = await ReadInitialRequestAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
            ValidateRequest(request);

            var systemPrompt = ResolveSystemPrompt(request.System);
            var messages = new List<GrokMessage>(BuildInitialMessages(systemPrompt, request.Context, request.Prompt));
            var remainingBudget = Math.Max(0, request.ToolTokenBudget);
            var totalToolTokens = 0;
            var maxRounds = request.MaxRounds > 0 ? request.MaxRounds : _options.DefaultMaxRounds;
            var degenerateLimit = Math.Max(1, _options.DegenerateToolCallLimit);
            var includeEncryptedReasoning = request.Effort == Effort.High;
            var completedStopReason = StopReason.Stop;
            var roundsCompleted = 0;
            var consecutiveToolSignature = "";
            var consecutiveToolCount = 0;
            var aggregateUsage = new Usage();

            activity?.SetTag("repoql.inference.max_rounds", maxRounds);
            activity?.SetTag("repoql.inference.tool_budget_remaining", remainingBudget);
            activity?.SetTag("repoql.inference.tool_count", request.Tools.Count);

            if (request.Tools.Count > 0 && remainingBudget == 0)
            {
                completedStopReason = StopReason.ToolBudget;
                messages.Add(CreateDeveloperMessage(
                    $"Tool token budget is exhausted (remaining: {remainingBudget}). Answer with the provided context only. Do not call tools."));
                _logger.LogInformation("Tool budget is zero at request start; forcing final answer without tools");
            }

            while (true)
            {
                activity?.SetTag("repoql.inference.round", roundsCompleted + 1);
                activity?.SetTag("repoql.inference.tool_budget_remaining", remainingBudget);

                GrokCompletionResult grokResult;
                try
                {
                    grokResult = await _grokClient.CompleteAsync(
                        new GrokCompletionRequest(
                            messages.ToArray(),
                            request.Effort,
                            request.MaxTokens > 0 ? request.MaxTokens : null,
                            request.Tools,
                            ResolveToolMode(request.Tools, remainingBudget, completedStopReason),
                            ParallelToolCalls: true,
                            IncludeEncryptedReasoningContent: includeEncryptedReasoning,
                            RoundNumber: roundsCompleted + 1),
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Client disconnected during tool loop");
                    return;
                }
                catch (GrokTimeoutException ex)
                {
                    _logger.LogWarning(ex, "Grok request timed out mid-loop");
                    throw new RpcException(new Status(StatusCode.Unavailable, "Grok API timed out — retry shortly"));
                }
                catch (GrokApiException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
                {
                    _logger.LogWarning(ex, "Grok rate limited the tool loop");
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Grok API rate limited — retry shortly"));
                }
                catch (GrokApiException ex)
                {
                    _logger.LogError(ex, "Grok request failed mid-loop");
                    throw new RpcException(new Status(StatusCode.Internal, "Grok API returned an error"));
                }

                AddUsage(aggregateUsage, grokResult.Usage);

                if (grokResult.ToolCalls.Count == 0 || ResolveToolMode(request.Tools, remainingBudget, completedStopReason) == GrokToolMode.None)
                {
                    await responseStream.WriteAsync(new ServerMessage
                    {
                        Completion = CreateCompletion(
                            grokResult,
                            completedStopReason == StopReason.Stop ? grokResult.StopReason : completedStopReason,
                            aggregateUsage,
                            totalToolTokens)
                    }).ConfigureAwait(false);
                    return;
                }

                var nextRound = roundsCompleted + 1;
                if (nextRound > maxRounds)
                {
                    completedStopReason = StopReason.ToolLimit;
                    messages.Add(CreateDeveloperMessage(
                        $"Maximum tool rounds reached ({maxRounds}). Answer with the gathered context and tool results. Do not call tools."));
                    _logger.LogInformation("Max rounds reached at round {Round}; forcing final answer", nextRound);
                    continue;
                }

                AppendAssistantToolCallMessage(messages, grokResult);

                var degenerateTriggered = false;
                foreach (var toolCall in grokResult.ToolCalls)
                {
                    var signature = $"{toolCall.Name}|{toolCall.ArgumentsJson}";
                    if (signature == consecutiveToolSignature)
                    {
                        consecutiveToolCount++;
                    }
                    else
                    {
                        consecutiveToolSignature = signature;
                        consecutiveToolCount = 1;
                    }

                    if (consecutiveToolCount >= degenerateLimit)
                    {
                        degenerateTriggered = true;
                        break;
                    }
                }

                if (degenerateTriggered)
                {
                    completedStopReason = StopReason.ToolLimit;
                    messages.Add(CreateDeveloperMessage(
                        $"The same tool call repeated {degenerateLimit} consecutive times. Answer with the gathered context and stop using tools."));
                    _logger.LogInformation("Degenerate tool loop detected after round {Round}; forcing final answer", nextRound);
                    continue;
                }

                roundsCompleted = nextRound;
                _logger.LogInformation(
                    "Starting tool round {Round} with {ToolCallCount} calls and {RemainingBudget} tokens remaining",
                    roundsCompleted,
                    grokResult.ToolCalls.Count,
                    remainingBudget);

                var acceptedRequests = new List<ToolRequest>();
                var toolResultMessages = new List<GrokMessage>();
                var reservedBudget = 0;

                foreach (var toolCall in grokResult.ToolCalls)
                {
                    if (!TryExtractTokenBudget(toolCall.ArgumentsJson, out var requestedBudget, out var parseError))
                    {
                        toolResultMessages.Add(CreateToolMessage(toolCall.Id, $"Error: malformed tool arguments. {parseError}"));
                        continue;
                    }

                    var availableBudget = remainingBudget - reservedBudget;
                    if (requestedBudget > availableBudget)
                    {
                        toolResultMessages.Add(CreateToolMessage(
                            toolCall.Id,
                            $"Error: requested tokenBudget {requestedBudget} exceeds remaining tool budget {availableBudget}. Request a smaller budget or answer with gathered context."));
                        continue;
                    }

                    reservedBudget += requestedBudget;
                    acceptedRequests.Add(new ToolRequest
                    {
                        CallId = toolCall.Id,
                        Round = roundsCompleted,
                        Tool = toolCall.Name,
                        ArgumentsJson = toolCall.ArgumentsJson
                    });
                }

                for (var i = 0; i < acceptedRequests.Count; i++)
                {
                    acceptedRequests[i].MoreInRound = i < acceptedRequests.Count - 1;
                    await responseStream.WriteAsync(new ServerMessage
                    {
                        ToolRequest = acceptedRequests[i]
                    }).ConfigureAwait(false);
                }

                if (acceptedRequests.Count > 0)
                {
                    var relayActivities = acceptedRequests.ToDictionary(
                        static requestItem => requestItem.CallId,
                        requestItem =>
                        {
                            var relayActivity = ActivitySource.StartActivity("inference.tool_relay");
                            relayActivity?.SetTag("repoql.inference.round", roundsCompleted);
                            relayActivity?.SetTag("repoql.inference.tool", requestItem.Tool);
                            relayActivity?.SetTag("repoql.inference.call_id", requestItem.CallId);
                            relayActivity?.SetTag("repoql.inference.tool_budget_remaining", remainingBudget);
                            return relayActivity;
                        });

                    try
                    {
                        var pendingCallIds = acceptedRequests.Select(static requestItem => requestItem.CallId).ToHashSet(StringComparer.Ordinal);
                        while (pendingCallIds.Count > 0)
                        {
                            var response = await ReadToolResponseAsync(requestStream, pendingCallIds, context.CancellationToken).ConfigureAwait(false);
                            pendingCallIds.Remove(response.CallId);

                            if (relayActivities.TryGetValue(response.CallId, out var relayActivity))
                            {
                                relayActivity?.SetTag("repoql.inference.tokens_used", response.TokensUsed);
                                relayActivity?.SetTag("repoql.inference.tool_error", response.IsError);
                                relayActivity?.Dispose();
                                relayActivities.Remove(response.CallId);
                            }

                            var usedTokens = Math.Max(0, response.TokensUsed);
                            remainingBudget = Math.Max(0, remainingBudget - usedTokens);
                            totalToolTokens += usedTokens;
                            _logger.LogInformation(
                                "Tool response {CallId} used {TokensUsed} tokens; remaining tool budget {RemainingBudget}",
                                response.CallId,
                                usedTokens,
                                remainingBudget);

                            toolResultMessages.Add(CreateToolMessage(
                                response.CallId,
                                response.IsError ? $"Error: {response.Content}" : response.Content));
                        }
                    }
                    finally
                    {
                        foreach (var relayActivity in relayActivities.Values)
                            relayActivity?.Dispose();
                    }
                }

                foreach (var toolResultMessage in toolResultMessages)
                    messages.Add(toolResultMessage);

                if (remainingBudget == 0 && completedStopReason != StopReason.ToolBudget)
                {
                    completedStopReason = StopReason.ToolBudget;
                    messages.Add(CreateDeveloperMessage(
                        "Tool token budget is exhausted. Answer with the gathered context and tool results. Do not call tools."));
                    _logger.LogInformation("Tool budget exhausted after round {Round}; forcing final answer", roundsCompleted);
                }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Client disconnected during CompleteWithTools");
        }
    }

    private static Completion CreateCompletion(GrokCompletionResult result, StopReason stopReason, Usage usage, int toolTokens)
    {
        return new Completion
        {
            Content = result.Content,
            Reasoning = result.Reasoning,
            StopReason = stopReason,
            Usage = new Usage
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                ThinkingTokens = usage.ThinkingTokens,
                ToolTokens = toolTokens
            },
            Model = result.Model
        };
    }

    private static string ResolveSystemPrompt(string? systemPrompt)
    {
        return string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt;
    }

    private static List<GrokMessage> BuildInitialMessages(string systemPrompt, string? context, string prompt)
    {
        var messages = new List<GrokMessage>
        {
            new(GrokMessageRole.Developer, systemPrompt)
        };

        if (!string.IsNullOrWhiteSpace(context))
            messages.Add(new GrokMessage(GrokMessageRole.User, context));

        messages.Add(new GrokMessage(GrokMessageRole.User, prompt));
        return messages;
    }

    private static GrokToolMode ResolveToolMode(RepeatedField<ToolDefinition> tools, int remainingBudget, StopReason completedStopReason)
    {
        if (tools.Count == 0 || remainingBudget == 0 || completedStopReason is StopReason.ToolBudget or StopReason.ToolLimit)
            return GrokToolMode.None;

        return GrokToolMode.Auto;
    }

    private static GrokMessage CreateDeveloperMessage(string content)
    {
        return new GrokMessage(GrokMessageRole.Developer, content);
    }

    private static GrokMessage CreateToolMessage(string callId, string content)
    {
        return new GrokMessage(GrokMessageRole.Tool, content, ToolCallId: callId);
    }

    private static void AppendAssistantToolCallMessage(List<GrokMessage> messages, GrokCompletionResult result)
    {
        messages.Add(new GrokMessage(
            GrokMessageRole.Assistant,
            string.IsNullOrWhiteSpace(result.Content) ? null : result.Content,
            result.ToolCalls,
            Reasoning: string.IsNullOrWhiteSpace(result.Reasoning) ? null : result.Reasoning,
            EncryptedContent: result.EncryptedContent));
    }

    private static bool TryExtractTokenBudget(string argumentsJson, out int tokenBudget, out string error)
    {
        tokenBudget = 0;
        error = "";

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Arguments must be a JSON object.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("tokenBudget", out var tokenBudgetElement))
            {
                error = "tokenBudget is required.";
                return false;
            }

            if (!tokenBudgetElement.TryGetInt32(out tokenBudget) || tokenBudget <= 0)
            {
                error = "tokenBudget must be a positive integer.";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task<CompleteRequest> ReadInitialRequestAsync(
        IAsyncStreamReader<ClientMessage> requestStream,
        CancellationToken cancellationToken)
    {
        if (!await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "First stream message must be CompleteRequest"));

        return requestStream.Current.MessageCase switch
        {
            ClientMessage.MessageOneofCase.Request => requestStream.Current.Request,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "First stream message must be CompleteRequest"))
        };
    }

    private async Task<ToolResponse> ReadToolResponseAsync(
        IAsyncStreamReader<ClientMessage> requestStream,
        HashSet<string> pendingCallIds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ToolResponseTimeoutSeconds));

        try
        {
            if (!await requestStream.MoveNext(timeoutCts.Token).ConfigureAwait(false))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Client completed the stream before sending all tool responses"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Timed out waiting for ToolResponse"));
        }

        return requestStream.Current.MessageCase switch
        {
            ClientMessage.MessageOneofCase.Request => throw new RpcException(new Status(StatusCode.InvalidArgument, "CompleteRequest may only be sent once")),
            ClientMessage.MessageOneofCase.ToolResponse when pendingCallIds.Contains(requestStream.Current.ToolResponse.CallId) => requestStream.Current.ToolResponse,
            ClientMessage.MessageOneofCase.ToolResponse => throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown call_id '{requestStream.Current.ToolResponse.CallId}'")),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "ClientMessage must contain ToolResponse"))
        };
    }

    private static void AddUsage(Usage aggregate, Usage usage)
    {
        aggregate.InputTokens += usage.InputTokens;
        aggregate.OutputTokens += usage.OutputTokens;
        aggregate.ThinkingTokens += usage.ThinkingTokens;
    }

    private static void ValidateRequest(CompleteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Prompt is required"));

        if (request.MaxTokens < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "max_tokens must be >= 0"));

        if (request.MaxRounds < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "max_rounds must be >= 0"));

        if (request.ToolTokenBudget < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "tool_token_budget must be >= 0"));
    }
}
