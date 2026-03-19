using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using RepoQL.Contracts.Cloud;
using RepoQL.Contracts.Inference;
using ProtoInference = RepoQL.Inference;

namespace RepoQL.Inference.Client;

/// <summary>
/// Purpose: Connects host consumers to the remote inference service over gRPC.
/// Complexity: Owns channel lifecycle, bearer token injection, proto-domain mapping,
/// typed error translation, and the bidirectional tool execution loop.
/// </summary>
public sealed class InferenceClient : IInferenceProvider, IDisposable
{
    private const int DefaultConnectTimeoutSeconds = 30;

    private readonly string _url;
    private readonly ICloudCredentialProvider _credentialProvider;
    private readonly ILogger<InferenceClient>? _logger;
    private readonly ProtoInference.InferenceService.InferenceServiceClient _client;
    private readonly IDisposable? _ownedResource;

    public InferenceClient(
        string url,
        ICloudCredentialProvider credentialProvider,
        ILogger<InferenceClient>? logger = null)
    {
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(DefaultConnectTimeoutSeconds)
        };

        var channel = GrpcChannel.ForAddress(_url, new GrpcChannelOptions
        {
            HttpHandler = handler
        });
        _ownedResource = channel;
        _client = new ProtoInference.InferenceService.InferenceServiceClient(channel);
    }

    internal InferenceClient(
        ProtoInference.InferenceService.InferenceServiceClient client,
        string url,
        ICloudCredentialProvider credentialProvider,
        ILogger<InferenceClient>? logger = null,
        IDisposable? ownedResource = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _credentialProvider = credentialProvider ?? throw new ArgumentNullException(nameof(credentialProvider));
        _logger = logger;
        _ownedResource = ownedResource;
    }

    public bool Available => !string.IsNullOrWhiteSpace(_url);

    public async Task<InferenceResult> CompleteAsync(
        InferenceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var response = await InvokeWithAuthRetryAsync(
                (headers, cancellationToken) => _client.CompleteAsync(
                        MapRequest(request),
                        headers: headers,
                        cancellationToken: cancellationToken)
                    .ResponseAsync,
                "Inference completion",
                ct).ConfigureAwait(false);

            return MapResult(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException rpcEx)
        {
            throw MapRpcException("Inference completion failed", rpcEx);
        }
    }

    public async Task<InferenceResult> CompleteWithToolsAsync(
        InferenceRequest request,
        ToolOptions toolOptions,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(toolOptions);
        ArgumentNullException.ThrowIfNull(executeTool);

        try
        {
            return await CompleteWithToolsWithAuthRetryAsync(request, toolOptions, executeTool, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RpcException rpcEx)
        {
            throw MapRpcException("Inference tool completion failed", rpcEx);
        }
    }

    private async Task<ProtoInference.ToolResponse> ExecuteToolAsync(
        ProtoInference.ToolRequest toolRequest,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct)
    {
        try
        {
            var result = await executeTool(new ToolCall
            {
                CallId = toolRequest.CallId,
                Tool = toolRequest.Tool,
                ArgumentsJson = toolRequest.ArgumentsJson
            }, ct).ConfigureAwait(false);

            return new ProtoInference.ToolResponse
            {
                CallId = toolRequest.CallId,
                Content = result.Content,
                IsError = result.IsError,
                TokensUsed = result.TokensUsed
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Inference tool execution failed for {Tool}", toolRequest.Tool);
            var errorContent = ex.Message;
            return new ProtoInference.ToolResponse
            {
                CallId = toolRequest.CallId,
                Content = errorContent,
                IsError = true,
                TokensUsed = Math.Max(1, errorContent.Length / 4)
            };
        }
    }

    private static ProtoInference.CompleteRequest MapRequest(
        InferenceRequest request,
        ToolOptions? toolOptions = null)
    {
        var proto = new ProtoInference.CompleteRequest
        {
            Prompt = request.Prompt ?? string.Empty,
            Context = request.Context ?? string.Empty,
            System = request.System ?? string.Empty,
            Effort = MapEffort(request.Effort),
            MaxTokens = request.MaxTokens
        };

        if (toolOptions is not null)
        {
            proto.ToolTokenBudget = toolOptions.ToolTokenBudget;
            proto.MaxRounds = toolOptions.MaxRounds;
            proto.Tools.AddRange(toolOptions.Tools.Select(tool => new ProtoInference.ToolDefinition
            {
                Name = tool.Name,
                Description = tool.Description,
                ParametersJson = tool.ParametersJson
            }));
        }

        return proto;
    }

    private static ProtoInference.Effort MapEffort(InferenceEffort effort)
        => effort switch
        {
            InferenceEffort.Low => ProtoInference.Effort.Low,
            InferenceEffort.High => ProtoInference.Effort.High,
            _ => ProtoInference.Effort.Balanced
        };

    private static InferenceResult MapResult(ProtoInference.Completion completion)
        => new()
        {
            Content = completion.Content,
            Reasoning = string.IsNullOrWhiteSpace(completion.Reasoning) ? null : completion.Reasoning,
            Model = string.IsNullOrWhiteSpace(completion.Model) ? null : completion.Model,
            InputTokens = completion.Usage?.InputTokens ?? 0,
            OutputTokens = completion.Usage?.OutputTokens ?? 0,
            ThinkingTokens = completion.Usage?.ThinkingTokens ?? 0,
            ToolTokens = completion.Usage?.ToolTokens ?? 0
        };

    private async Task<InferenceResult> CompleteWithToolsWithAuthRetryAsync(
        InferenceRequest request,
        ToolOptions toolOptions,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct)
    {
        var toolExecutionStarted = false;

        try
        {
            return await CompleteWithToolsCoreAsync(
                    MapRequest(request, toolOptions),
                    await GetAuthHeadersAsync(ct).ConfigureAwait(false),
                    () => toolExecutionStarted = true,
                    executeTool,
                    ct)
                .ConfigureAwait(false);
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unauthenticated && !toolExecutionStarted)
        {
            _logger?.LogInformation(
                "Inference tool completion was rejected as unauthenticated; forcing token refresh and retrying once.");

            return await CompleteWithToolsCoreAsync(
                    MapRequest(request, toolOptions),
                    await GetRefreshedAuthHeadersAsync(ct).ConfigureAwait(false),
                    () => toolExecutionStarted = true,
                    executeTool,
                    ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<InferenceResult> CompleteWithToolsCoreAsync(
        ProtoInference.CompleteRequest request,
        Metadata headers,
        Action onToolExecutionStarted,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> executeTool,
        CancellationToken ct)
    {
        using var call = _client.CompleteWithTools(headers: headers, cancellationToken: ct);

        await call.RequestStream.WriteAsync(new ProtoInference.ClientMessage
        {
            Request = request
        }).ConfigureAwait(false);

        while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
        {
            var serverMessage = call.ResponseStream.Current;
            switch (serverMessage.MessageCase)
            {
                case ProtoInference.ServerMessage.MessageOneofCase.Completion:
                    await SafeCompleteRequestStreamAsync(call).ConfigureAwait(false);
                    return MapResult(serverMessage.Completion);

                case ProtoInference.ServerMessage.MessageOneofCase.ToolRequest:
                {
                    onToolExecutionStarted();

                    var round = new List<ProtoInference.ToolRequest> { serverMessage.ToolRequest };

                    while (round[^1].MoreInRound)
                    {
                        if (!await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
                            throw new InferenceException("Inference stream ended before completing the tool round.");

                        var next = call.ResponseStream.Current;
                        if (next.MessageCase == ProtoInference.ServerMessage.MessageOneofCase.Completion)
                        {
                            await SafeCompleteRequestStreamAsync(call).ConfigureAwait(false);
                            return MapResult(next.Completion);
                        }

                        if (next.MessageCase != ProtoInference.ServerMessage.MessageOneofCase.ToolRequest)
                            throw new InferenceException($"Unexpected inference stream message: {next.MessageCase}.");

                        round.Add(next.ToolRequest);
                    }

                    var responses = await Task.WhenAll(round.Select(toolRequest =>
                            ExecuteToolAsync(toolRequest, executeTool, ct)))
                        .ConfigureAwait(false);

                    foreach (var response in responses)
                    {
                        await call.RequestStream.WriteAsync(new ProtoInference.ClientMessage
                        {
                            ToolResponse = response
                        }).ConfigureAwait(false);
                    }

                    break;
                }

                default:
                    throw new InferenceException($"Unexpected inference stream message: {serverMessage.MessageCase}.");
            }
        }

        throw new InferenceException("Inference stream completed without a final completion.");
    }

    private async Task<T> InvokeWithAuthRetryAsync<T>(
        Func<Metadata, CancellationToken, Task<T>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(await GetAuthHeadersAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcException rpcEx) when (rpcEx.StatusCode == StatusCode.Unauthenticated)
        {
            _logger?.LogInformation(
                "{Operation} was rejected as unauthenticated; forcing token refresh and retrying once.",
                operationName);

            return await operation(await GetRefreshedAuthHeadersAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<Metadata> GetAuthHeadersAsync(CancellationToken cancellationToken)
        => new()
        {
            { "authorization", $"Bearer {await _credentialProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false)}" }
        };

    private async Task<Metadata> GetRefreshedAuthHeadersAsync(CancellationToken cancellationToken)
        => new()
        {
            { "authorization", $"Bearer {await _credentialProvider.RefreshTokenAsync(cancellationToken).ConfigureAwait(false)}" }
        };

    private Exception MapRpcException(string operation, RpcException rpcEx)
    {
        var detail = string.IsNullOrWhiteSpace(rpcEx.Status.Detail) ? rpcEx.Message : rpcEx.Status.Detail;
        return rpcEx.StatusCode switch
        {
            StatusCode.DeadlineExceeded => new InferenceTimeoutException(
                $"{operation}: inference service timed out at {_url}. {detail}",
                rpcEx),
            StatusCode.Unavailable => new InferenceUnavailableException(
                $"{operation}: inference service unreachable at {_url}. {detail}",
                rpcEx),
            _ => new InferenceException($"{operation}: {detail}", rpcEx)
        };
    }

    private static async Task SafeCompleteRequestStreamAsync(
        AsyncDuplexStreamingCall<ProtoInference.ClientMessage, ProtoInference.ServerMessage> call)
    {
        try
        {
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
            // Stream may already be closed by the server; disposal will finish cleanup.
        }
    }

    public void Dispose() => _ownedResource?.Dispose();
}
