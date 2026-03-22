using AwesomeAssertions;
using FakeItEasy;
using Grpc.Core;
using RepoQL.Contracts.Cloud;
using RepoQL.Contracts.Inference;
using RepoQL.Inference.Client;
using ProtoInference = RepoQL.Inference;

namespace RepoQL.Inference.Client.Tests;

public sealed class InferenceClientTests
{
    private static readonly ICloudCredentialProvider StaticProvider = new StubCloudCredentialProvider("secret-token");

    [Test]
    public async Task CompleteAsync_MapsProtoRequestAndResponse()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        ProtoInference.CompleteRequest? capturedRequest = null;
        Metadata? capturedHeaders = null;

        A.CallTo(() => grpcClient.CompleteAsync(
                A<ProtoInference.CompleteRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Invokes(call =>
            {
                capturedRequest = call.GetArgument<ProtoInference.CompleteRequest>(0);
                capturedHeaders = call.GetArgument<Metadata?>(1);
            })
            .Returns(CreateUnaryCall(new ProtoInference.Completion
            {
                Content = "answer",
                Reasoning = "reasoning",
                Model = "grok-fast",
                Usage = new ProtoInference.Usage
                {
                    InputTokens = 11,
                    OutputTokens = 7,
                    ThinkingTokens = 3,
                    ToolTokens = 5
                }
            }));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        var result = await client.CompleteAsync(new InferenceRequest
        {
            Prompt = "Explain this",
            Context = "context",
            System = "system",
            Effort = InferenceEffort.High,
            MaxTokens = 900
        });

        result.Content.Should().Be("answer");
        result.Reasoning.Should().Be("reasoning");
        result.Model.Should().Be("grok-fast");
        result.InputTokens.Should().Be(11);
        result.OutputTokens.Should().Be(7);
        result.ThinkingTokens.Should().Be(3);
        result.ToolTokens.Should().Be(5);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Prompt.Should().Be("Explain this");
        capturedRequest.Context.Should().Be("context");
        capturedRequest.System.Should().Be("system");
        capturedRequest.Effort.Should().Be(ProtoInference.Effort.High);
        capturedRequest.MaxTokens.Should().Be(900);
        capturedHeaders.Should().NotBeNull();
        capturedHeaders!.Single(h => h.Key == "authorization").Value.Should().Be("Bearer secret-token");
    }

    [Test]
    public async Task CompleteWithToolsAsync_ExecutesToolRoundAndReturnsCompletion()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var requestWriter = new RecordingClientStreamWriter<ProtoInference.ClientMessage>();
        var responseReader = new QueueAsyncStreamReader<ProtoInference.ServerMessage>(
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "call-1",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/a.cs\",\"tokenBudget\":80}",
                    Round = 1,
                    MoreInRound = true
                }
            },
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "call-2",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/b.cs\",\"tokenBudget\":40}",
                    Round = 1,
                    MoreInRound = false
                }
            },
            new ProtoInference.ServerMessage
            {
                Completion = new ProtoInference.Completion
                {
                    Content = "final answer",
                    Reasoning = "trace",
                    Model = "grok",
                    Usage = new ProtoInference.Usage
                    {
                        InputTokens = 20,
                        OutputTokens = 10,
                        ThinkingTokens = 4,
                        ToolTokens = 22
                    }
                }
            });

        A.CallTo(() => grpcClient.CompleteWithTools(
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateDuplexCall(requestWriter, responseReader));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        var observedCalls = new List<ToolCall>();
        var result = await client.CompleteWithToolsAsync(
            new InferenceRequest
            {
                Prompt = "Explain",
                Context = "explore output",
                Effort = InferenceEffort.Balanced,
                MaxTokens = 500
            },
            new ToolOptions
            {
                Tools =
                [
                    new InferenceToolDefinition
                    {
                        Name = "read",
                        Description = "Read content",
                        ParametersJson = "{\"type\":\"object\"}"
                    }
                ],
                ToolTokenBudget = 120,
                MaxRounds = 3
            },
            (toolCall, _) =>
            {
                observedCalls.Add(toolCall);
                return Task.FromResult(new ToolCallResult
                {
                    Content = $"result for {toolCall.CallId}",
                    TokensUsed = toolCall.CallId == "call-1" ? 12 : 10
                });
            });

        result.Content.Should().Be("final answer");
        result.Reasoning.Should().Be("trace");
        result.ToolTokens.Should().Be(22);
        observedCalls.Select(call => call.CallId).Should().BeEquivalentTo(["call-1", "call-2"]);

        requestWriter.Messages.Should().HaveCount(3);
        requestWriter.Messages[0].Request.Should().NotBeNull();
        requestWriter.Messages[0].Request.Tools.Should().HaveCount(1);
        requestWriter.Messages[1].ToolResponse.CallId.Should().Be("call-1");
        requestWriter.Messages[1].ToolResponse.Content.Should().Be("result for call-1");
        requestWriter.Messages[1].ToolResponse.TokensUsed.Should().Be(12);
        requestWriter.Messages[2].ToolResponse.CallId.Should().Be("call-2");
        requestWriter.Messages[2].ToolResponse.Content.Should().Be("result for call-2");
        requestWriter.Completed.Should().BeTrue();
    }

    [Test]
    public async Task CompleteWithToolsAsync_PreservesCallbackErrorsInToolResponses()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var requestWriter = new RecordingClientStreamWriter<ProtoInference.ClientMessage>();
        var responseReader = new QueueAsyncStreamReader<ProtoInference.ServerMessage>(
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "call-1",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/a.cs => question: recurse\",\"tokenBudget\":80}",
                    Round = 1,
                    MoreInRound = false
                }
            },
            new ProtoInference.ServerMessage
            {
                Completion = new ProtoInference.Completion
                {
                    Content = "done"
                }
            });

        A.CallTo(() => grpcClient.CompleteWithTools(
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateDuplexCall(requestWriter, responseReader));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        await client.CompleteWithToolsAsync(
            new InferenceRequest { Prompt = "Explain" },
            new ToolOptions
            {
                Tools =
                [
                    new InferenceToolDefinition
                    {
                        Name = "read",
                        Description = "Read content",
                        ParametersJson = "{\"type\":\"object\"}"
                    }
                ]
            },
            (toolCall, _) => Task.FromResult(new ToolCallResult
            {
                Content = "read => question: is not allowed during inference tool execution",
                IsError = true,
                TokensUsed = 0
            }));

        requestWriter.Messages.Should().HaveCount(2);
        requestWriter.Messages[1].ToolResponse.IsError.Should().BeTrue();
        requestWriter.Messages[1].ToolResponse.Content.Should().Contain("question:");
    }

    [Test]
    public async Task CompleteAsync_MapsUnavailableRpcToTypedException()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var rpcException = new RpcException(new Status(StatusCode.Unavailable, "connection refused"));

        A.CallTo(() => grpcClient.CompleteAsync(
                A<ProtoInference.CompleteRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateFaultedUnaryCall<ProtoInference.Completion>(rpcException));

        using var client = new InferenceClient(grpcClient, "https://offline.example", StaticProvider);

        var exception = await Assert.That(async () => await client.CompleteAsync(new InferenceRequest
        {
            Prompt = "Explain"
        })).Throws<InferenceUnavailableException>();

        exception!.Message.Should().Contain("offline.example");
    }

    [Test]
    public async Task CompleteAsync_MapsDeadlineExceededToTimeoutException()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var rpcException = new RpcException(new Status(StatusCode.DeadlineExceeded, "request timed out"));

        A.CallTo(() => grpcClient.CompleteAsync(
                A<ProtoInference.CompleteRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateFaultedUnaryCall<ProtoInference.Completion>(rpcException));

        using var client = new InferenceClient(grpcClient, "https://slow.example", StaticProvider);

        var exception = await Assert.That(async () => await client.CompleteAsync(new InferenceRequest
        {
            Prompt = "Explain"
        })).Throws<InferenceTimeoutException>();

        exception!.Message.Should().Contain("slow.example");
    }

    [Test]
    public async Task CompleteAsync_RetriesOnceOnUnauthenticatedWithRefreshedToken()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var credentialProvider = new RefreshingCloudCredentialProvider("expired-token", "fresh-token");
        var headers = new List<string?>();
        var calls = 0;

        A.CallTo(() => grpcClient.CompleteAsync(
                A<ProtoInference.CompleteRequest>.Ignored,
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .ReturnsLazily(call =>
            {
                calls++;
                headers.Add(call.GetArgument<Metadata?>(1)?.SingleOrDefault(h => h.Key == "authorization").Value);
                return calls == 1
                    ? CreateFaultedUnaryCall<ProtoInference.Completion>(
                        new RpcException(new Status(StatusCode.Unauthenticated, "expired")))
                    : CreateUnaryCall(new ProtoInference.Completion { Content = "refreshed" });
            });

        using var client = new InferenceClient(grpcClient, "https://inference.example", credentialProvider);

        var result = await client.CompleteAsync(new InferenceRequest { Prompt = "Explain" });

        result.Content.Should().Be("refreshed");
        headers.Should().Equal(["Bearer expired-token", "Bearer fresh-token"]);
        credentialProvider.RefreshCallCount.Should().Be(1);
    }

    [Test]
    public async Task CompleteWithToolsAsync_ToolCallbackThrowingMarshalledAsError()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var requestWriter = new RecordingClientStreamWriter<ProtoInference.ClientMessage>();
        var responseReader = new QueueAsyncStreamReader<ProtoInference.ServerMessage>(
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "call-1",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/a.cs\",\"tokenBudget\":80}",
                    Round = 1,
                    MoreInRound = false
                }
            },
            new ProtoInference.ServerMessage
            {
                Completion = new ProtoInference.Completion
                {
                    Content = "final answer"
                }
            });

        A.CallTo(() => grpcClient.CompleteWithTools(
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateDuplexCall(requestWriter, responseReader));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        var result = await client.CompleteWithToolsAsync(
            new InferenceRequest { Prompt = "Explain" },
            new ToolOptions
            {
                Tools =
                [
                    new InferenceToolDefinition
                    {
                        Name = "read",
                        Description = "Read content",
                        ParametersJson = "{\"type\":\"object\"}"
                    }
                ]
            },
            (_, _) => throw new InvalidOperationException("tool exploded"));

        result.Content.Should().Be("final answer");
        requestWriter.Messages.Should().HaveCount(2);
        requestWriter.Messages[1].ToolResponse.IsError.Should().BeTrue();
        requestWriter.Messages[1].ToolResponse.Content.Should().Contain("tool exploded");
        requestWriter.Messages[1].ToolResponse.TokensUsed.Should().BeGreaterThan(0);
        requestWriter.Completed.Should().BeTrue();
    }

    [Test]
    public async Task CompleteWithToolsAsync_MultipleRoundsExecuteCorrectly()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var requestWriter = new RecordingClientStreamWriter<ProtoInference.ClientMessage>();
        var responseReader = new QueueAsyncStreamReader<ProtoInference.ServerMessage>(
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "round-1",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/one.cs\",\"tokenBudget\":80}",
                    Round = 1,
                    MoreInRound = false
                }
            },
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "round-2",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/two.cs\",\"tokenBudget\":60}",
                    Round = 2,
                    MoreInRound = false
                }
            },
            new ProtoInference.ServerMessage
            {
                Completion = new ProtoInference.Completion
                {
                    Content = "done"
                }
            });

        A.CallTo(() => grpcClient.CompleteWithTools(
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateDuplexCall(requestWriter, responseReader));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        var observedCalls = new List<string>();
        var result = await client.CompleteWithToolsAsync(
            new InferenceRequest { Prompt = "Explain" },
            new ToolOptions
            {
                Tools =
                [
                    new InferenceToolDefinition
                    {
                        Name = "read",
                        Description = "Read content",
                        ParametersJson = "{\"type\":\"object\"}"
                    }
                ]
            },
            (toolCall, _) =>
            {
                observedCalls.Add(toolCall.CallId);
                return Task.FromResult(new ToolCallResult
                {
                    Content = $"response for {toolCall.CallId}",
                    TokensUsed = 9
                });
            });

        result.Content.Should().Be("done");
        observedCalls.Should().Equal(["round-1", "round-2"]);
        requestWriter.Messages.Should().HaveCount(3);
        requestWriter.Messages[1].ToolResponse.CallId.Should().Be("round-1");
        requestWriter.Messages[1].ToolResponse.Content.Should().Be("response for round-1");
        requestWriter.Messages[2].ToolResponse.CallId.Should().Be("round-2");
        requestWriter.Messages[2].ToolResponse.Content.Should().Be("response for round-2");
        requestWriter.Completed.Should().BeTrue();
    }

    [Test]
    public async Task CompleteWithToolsAsync_StreamEndsMidRoundThrows()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var requestWriter = new RecordingClientStreamWriter<ProtoInference.ClientMessage>();
        var responseReader = new QueueAsyncStreamReader<ProtoInference.ServerMessage>(
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "call-1",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/a.cs\",\"tokenBudget\":80}",
                    Round = 1,
                    MoreInRound = true
                }
            });

        A.CallTo(() => grpcClient.CompleteWithTools(
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateDuplexCall(requestWriter, responseReader));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        var exception = await Assert.That(async () => await client.CompleteWithToolsAsync(
            new InferenceRequest { Prompt = "Explain" },
            new ToolOptions { Tools = [] },
            (_, _) => Task.FromResult(new ToolCallResult { Content = string.Empty }))).Throws<InferenceException>();

        exception!.Message.Should().Contain("stream ended");
    }

    [Test]
    public async Task CompleteWithToolsAsync_CompletionArrivingMidRoundReturnsEarly()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var requestWriter = new RecordingClientStreamWriter<ProtoInference.ClientMessage>();
        var responseReader = new QueueAsyncStreamReader<ProtoInference.ServerMessage>(
            new ProtoInference.ServerMessage
            {
                ToolRequest = new ProtoInference.ToolRequest
                {
                    CallId = "call-1",
                    Tool = "read",
                    ArgumentsJson = "{\"uriGlob\":\"file:///src/a.cs\",\"tokenBudget\":80}",
                    Round = 1,
                    MoreInRound = true
                }
            },
            new ProtoInference.ServerMessage
            {
                Completion = new ProtoInference.Completion
                {
                    Content = "early completion",
                    Usage = new ProtoInference.Usage
                    {
                        ToolTokens = 4
                    }
                }
            });

        A.CallTo(() => grpcClient.CompleteWithTools(
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateDuplexCall(requestWriter, responseReader));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        var executed = false;
        var result = await client.CompleteWithToolsAsync(
            new InferenceRequest { Prompt = "Explain" },
            new ToolOptions { Tools = [] },
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(new ToolCallResult { Content = string.Empty });
            });

        result.Content.Should().Be("early completion");
        result.ToolTokens.Should().Be(4);
        executed.Should().BeFalse();
        requestWriter.Messages.Should().HaveCount(1);
        requestWriter.Completed.Should().BeTrue();
    }

    [Test]
    public async Task CompleteWithToolsAsync_UnexpectedMessageTypeThrows()
    {
        var grpcClient = A.Fake<ProtoInference.InferenceService.InferenceServiceClient>();
        var requestWriter = new RecordingClientStreamWriter<ProtoInference.ClientMessage>();
        var responseReader = new QueueAsyncStreamReader<ProtoInference.ServerMessage>(
            new ProtoInference.ServerMessage());

        A.CallTo(() => grpcClient.CompleteWithTools(
                A<Metadata?>.Ignored,
                A<DateTime?>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(CreateDuplexCall(requestWriter, responseReader));

        using var client = new InferenceClient(grpcClient, "https://inference.example", StaticProvider);

        var exception = await Assert.That(async () => await client.CompleteWithToolsAsync(
            new InferenceRequest { Prompt = "Explain" },
            new ToolOptions { Tools = [] },
            (_, _) => Task.FromResult(new ToolCallResult { Content = string.Empty }))).Throws<InferenceException>();

        exception!.Message.Should().Contain("Unexpected inference stream message");
    }

    [Test]
    public async Task DisabledInferenceProvider_ReturnsGracefulMessage()
    {
        var provider = new DisabledInferenceProvider();

        var result = await provider.CompleteAsync(new InferenceRequest
        {
            Prompt = "Explain"
        });

        provider.Available.Should().BeFalse();
        result.Content.Should().Contain("Inference service not configured");
    }

    private static AsyncUnaryCall<T> CreateUnaryCall<T>(T response)
        where T : class
        => new(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    private static AsyncUnaryCall<T> CreateFaultedUnaryCall<T>(RpcException exception)
        where T : class
        => new(
            Task.FromException<T>(exception),
            Task.FromResult(new Metadata()),
            () => exception.Status,
            () => new Metadata(),
            () => { });

    private static AsyncDuplexStreamingCall<TRequest, TResponse> CreateDuplexCall<TRequest, TResponse>(
        IClientStreamWriter<TRequest> requestWriter,
        IAsyncStreamReader<TResponse> responseReader)
        where TRequest : class
        where TResponse : class
        => new(
            requestWriter,
            responseReader,
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

    private sealed class RecordingClientStreamWriter<T> : IClientStreamWriter<T>
        where T : class
    {
        public List<T> Messages { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public bool Completed { get; private set; }

        public Task WriteAsync(T message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class QueueAsyncStreamReader<T>(params T[] messages) : IAsyncStreamReader<T>
        where T : class
    {
        private readonly Queue<T> _messages = new(messages);

        public T Current { get; private set; } = null!;

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_messages.Count == 0)
                return Task.FromResult(false);

            Current = _messages.Dequeue();
            return Task.FromResult(true);
        }
    }

    private sealed class StubCloudCredentialProvider(string token) : ICloudCredentialProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);

        public Task<string> RefreshTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);
    }

    private sealed class RefreshingCloudCredentialProvider(string initialToken, string refreshedToken) : ICloudCredentialProvider
    {
        public int RefreshCallCount { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(initialToken);

        public Task<string> RefreshTokenAsync(CancellationToken cancellationToken = default)
        {
            RefreshCallCount++;
            return Task.FromResult(refreshedToken);
        }
    }
}
