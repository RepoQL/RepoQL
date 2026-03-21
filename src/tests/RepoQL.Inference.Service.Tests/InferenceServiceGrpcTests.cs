using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using AwesomeAssertions;
using FakeItEasy;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RepoQL.Cloud.Auth;

namespace RepoQL.Inference.Service.Tests;

public sealed class InferenceServiceGrpcTests
{
    private const string TestAuthToken = "rql_expected-token";

    [Test]
    public async Task Complete_UsesDefaultSystemPromptAndIgnoresUnaryTools()
    {
        var grokClient = A.Fake<IGrokClient>();
        GrokCompletionRequest? capturedRequest = null;

        A.CallTo(() => grokClient.CompleteAsync(A<GrokCompletionRequest>.Ignored, A<CancellationToken>.Ignored))
            .Invokes(call => capturedRequest = call.GetArgument<GrokCompletionRequest>(0))
            .Returns(CompletionResult("answer", "reasoning", inputTokens: 21, outputTokens: 13, thinkingTokens: 5));

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        var response = await client.CompleteAsync(new CompleteRequest
        {
            Prompt = "What changed?",
            Context = "diff here",
            Effort = Effort.Balanced,
            Tools =
            {
                CreateReadTool()
            },
            ToolTokenBudget = 500,
            MaxRounds = 3
        });

        await Assert.That(response.Content).IsEqualTo("answer");
        await Assert.That(response.Reasoning).IsEqualTo("reasoning");
        await Assert.That(response.Model).IsEqualTo("grok-4-1-fast-non-reasoning");
        await Assert.That(response.Usage.InputTokens).IsEqualTo(21);
        await Assert.That(capturedRequest).IsNotNull();
        await Assert.That(capturedRequest!.Messages.Count).IsEqualTo(3);
        await Assert.That(capturedRequest.Messages[0].Content).IsEqualTo(InferenceServiceImpl.DefaultSystemPrompt);
        await Assert.That(capturedRequest.Messages[1].Content).IsEqualTo("diff here");
        await Assert.That(capturedRequest.Messages[2].Content).IsEqualTo("What changed?");
        await Assert.That(capturedRequest.Tools.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Complete_RejectsEmptyPrompt()
    {
        var grokClient = A.Fake<IGrokClient>();
        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        var exception = await Assert.That(async () => await client.CompleteAsync(new CompleteRequest()))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task Complete_RejectsMissingUnaryAuthorizationHeader()
    {
        var grokClient = A.Fake<IGrokClient>();
        await using var server = await InferenceTestHost.StartAsync(grokClient, [ComputeHash("rql_expected-token")]);
        var client = server.CreateClient(authenticated: false);

        var exception = await Assert.That(async () => await client.CompleteAsync(new CompleteRequest { Prompt = "hi" }))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task Complete_AcceptsValidUnaryAuthorizationHeader()
    {
        var grokClient = A.Fake<IGrokClient>();
        A.CallTo(() => grokClient.CompleteAsync(A<GrokCompletionRequest>.Ignored, A<CancellationToken>.Ignored))
            .Returns(CompletionResult("ok"));

        await using var server = await InferenceTestHost.StartAsync(grokClient, [ComputeHash("rql_expected-token")]);
        var client = server.CreateClient(authenticated: false);
        var headers = new Metadata { { "authorization", "Bearer rql_expected-token" } };

        var response = await client.CompleteAsync(new CompleteRequest { Prompt = "hi" }, headers);

        await Assert.That(response.Content).IsEqualTo("ok");
    }

    [Test]
    public async Task CompleteWithTools_RejectsMissingStreamingAuthorizationHeader()
    {
        var grokClient = A.Fake<IGrokClient>();
        await using var server = await InferenceTestHost.StartAsync(grokClient, [ComputeHash("rql_expected-token")]);
        var client = server.CreateClient(authenticated: false);

        var exception = await Assert.That(async () => await InvokeStreamingAsync(client, null))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task CompleteWithTools_CompletesToolLoopAndAggregatesUsage()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":100}")],
                inputTokens: 10,
                outputTokens: 2,
                thinkingTokens: 1)),
            static (_, _) => Task.FromResult(CompletionResult(
                "final answer",
                "final reasoning",
                inputTokens: 20,
                outputTokens: 3,
                thinkingTokens: 4)));

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(toolTokenBudget: 250) });

        var toolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await Assert.That(toolRequest.CallId).IsEqualTo("call-1");
        await Assert.That(toolRequest.Round).IsEqualTo(1);
        await Assert.That(toolRequest.Tool).IsEqualTo("read");
        await Assert.That(toolRequest.MoreInRound).IsFalse();

        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = "call-1",
                Content = "file content",
                TokensUsed = 80
            }
        });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("final answer");
        await Assert.That(completion.Reasoning).IsEqualTo("final reasoning");
        await Assert.That(completion.Usage.InputTokens).IsEqualTo(30);
        await Assert.That(completion.Usage.OutputTokens).IsEqualTo(5);
        await Assert.That(completion.Usage.ThinkingTokens).IsEqualTo(5);
        await Assert.That(completion.Usage.ToolTokens).IsEqualTo(80);
    }

    [Test]
    public async Task CompleteWithTools_RejectsMissingInitialRequest()
    {
        var grokClient = A.Fake<IGrokClient>();
        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse { CallId = "call-1", Content = "oops" }
        });
        await call.RequestStream.CompleteAsync();

        var exception = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task CompleteWithTools_RejectsDuplicateRequest()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":50}")])));

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest() });

        var toolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await Assert.That(toolRequest.CallId).IsEqualTo("call-1");

        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(prompt: "duplicate") });

        var exception = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task CompleteWithTools_RejectsUnknownToolResponseCallId()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":50}")])));

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest() });

        _ = await ReadNextServerMessageAsync(call);
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = "wrong-call",
                Content = "wrong"
            }
        });

        var exception = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task CompleteWithTools_RejectsToolCallThatExceedsRemainingBudgetBeforeDispatch()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":200}")])),
            static (request, _) =>
            {
                request.Messages[^1].Role.Should().Be(GrokMessageRole.Tool);
                request.Messages[^1].Content.Should().Contain("exceeds remaining tool budget 100");
                return Task.FromResult(CompletionResult("answer from context"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(toolTokenBudget: 100) });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("answer from context");
    }

    [Test]
    public async Task CompleteWithTools_DeductsTokensUsedFromBudgetPool()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":40}")])),
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-2", "read", "{\"uri\":\"file:///src/b.cs\",\"tokenBudget\":30}")])),
            static (request, _) =>
            {
                request.Messages[^1].Content.Should().Contain("remaining tool budget 20");
                return Task.FromResult(CompletionResult("budget respected"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(toolTokenBudget: 100) });

        var firstToolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await Assert.That(firstToolRequest.CallId).IsEqualTo("call-1");

        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = "call-1",
                Content = "result a",
                TokensUsed = 80
            }
        });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("budget respected");
        await Assert.That(completion.Usage.ToolTokens).IsEqualTo(80);
    }

    [Test]
    public async Task CompleteWithTools_DisablesToolsWhenBudgetIsZero()
    {
        var grokClient = new ScriptedGrokClient(
            static (request, _) =>
            {
                request.ToolMode.Should().Be(GrokToolMode.None);
                request.Messages[^1].Content.Should().Contain("Tool token budget is exhausted");
                return Task.FromResult(CompletionResult("direct answer"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(toolTokenBudget: 0) });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("direct answer");
        await Assert.That(completion.StopReason).IsEqualTo(StopReason.ToolBudget);
    }

    [Test]
    public async Task CompleteWithTools_ReportsMissingTokenBudgetAsMalformedToolError()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\"}")])),
            static (request, _) =>
            {
                request.Messages[^1].Content.Should().Contain("tokenBudget is required");
                return Task.FromResult(CompletionResult("recovered"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest() });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("recovered");
    }

    [Test]
    public async Task CompleteWithTools_PartiallyRejectsParallelBatchAndSetsMoreInRound()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [
                    new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":60}"),
                    new GrokFunctionCall("call-2", "read", "{\"uri\":\"file:///src/b.cs\",\"tokenBudget\":50}")
                ])),
            static (request, _) =>
            {
                request.Messages.Should().HaveCount(6);
                request.Messages[4].Role.Should().Be(GrokMessageRole.Tool);
                request.Messages[4].Content.Should().Contain("exceeds remaining tool budget 40");
                request.Messages[5].Content.Should().Be("result a");
                return Task.FromResult(CompletionResult("partial batch handled"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(toolTokenBudget: 100) });

        var first = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await Assert.That(first.CallId).IsEqualTo("call-1");
        await Assert.That(first.MoreInRound).IsFalse();

        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = "call-1",
                Content = "result a",
                TokensUsed = 30
            }
        });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("partial batch handled");
    }

    [Test]
    public async Task CompleteWithTools_EnforcesMaxRounds()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":20}")])),
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-2", "read", "{\"uri\":\"file:///src/b.cs\",\"tokenBudget\":20}")])),
            static (request, _) =>
            {
                request.ToolMode.Should().Be(GrokToolMode.None);
                request.Messages[^1].Content.Should().Contain("Maximum tool rounds reached");
                return Task.FromResult(CompletionResult("final after max rounds"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, [], options => options.DefaultMaxRounds = 1);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(maxRounds: 1) });

        var firstToolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await Assert.That(firstToolRequest.CallId).IsEqualTo("call-1");

        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = "call-1",
                Content = "result a",
                TokensUsed = 10
            }
        });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("final after max rounds");
        await Assert.That(completion.StopReason).IsEqualTo(StopReason.ToolLimit);
    }

    [Test]
    public async Task CompleteWithTools_SetsMoreInRoundForParallelRequests()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [
                    new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":20}"),
                    new GrokFunctionCall("call-2", "read", "{\"uri\":\"file:///src/b.cs\",\"tokenBudget\":20}")
                ])),
            static (_, _) => Task.FromResult(CompletionResult("parallel complete")));

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest(toolTokenBudget: 100) });

        var first = (await ReadNextServerMessageAsync(call)).ToolRequest;
        var second = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await Assert.That(first.MoreInRound).IsTrue();
        await Assert.That(second.MoreInRound).IsFalse();

        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = "call-2",
                Content = "result b",
                TokensUsed = 10
            }
        });
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = "call-1",
                Content = "result a",
                TokensUsed = 10
            }
        });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("parallel complete");
    }

    [Test]
    public async Task CompleteWithTools_PreservesEncryptedContentAcrossRoundsForHighEffort()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":20}")],
                encryptedContent: "cipher-1")),
            static (request, _) =>
            {
                request.Messages[3].EncryptedContent.Should().Be("cipher-1");
                request.IncludeEncryptedReasoningContent.Should().BeTrue();
                return Task.FromResult(CompletionResult("high effort complete"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            Request = CreateToolLoopRequest(toolTokenBudget: 100, effort: Effort.High)
        });

        var toolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = toolRequest.CallId,
                Content = "result a",
                TokensUsed = 10
            }
        });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("high effort complete");
    }

    [Test]
    public async Task CompleteWithTools_ReportsMalformedJsonArgumentsAsToolError()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{not json}")])),
            static (request, _) =>
            {
                request.Messages[^1].Content.Should().Contain("malformed tool arguments");
                return Task.FromResult(CompletionResult("json recovered"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest() });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("json recovered");
    }

    [Test]
    public async Task CompleteWithTools_CancelsOutstandingGrokCallWhenClientDisconnects()
    {
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":20}")])),
            async (_, cancellationToken) =>
            {
                secondCallStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return CompletionResult("unexpected");
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult(true);
                    throw;
                }
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest() });

        var toolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = toolRequest.CallId,
                Content = "result a",
                TokensUsed = 10
            }
        });

        await secondCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        call.Dispose();
        var cancelled = await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cancelled).IsTrue();
    }

    [Test]
    public async Task CompleteWithTools_DetectsDegenerateLoop()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":20}")])),
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-2", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":20}")])),
            static (request, _) =>
            {
                request.ToolMode.Should().Be(GrokToolMode.None);
                request.Messages[^1].Content.Should().Contain("same tool call repeated 2 consecutive times");
                return Task.FromResult(CompletionResult("degenerate stopped"));
            });

        await using var server = await InferenceTestHost.StartAsync(grokClient, [], options => options.DegenerateToolCallLimit = 2);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest() });

        var firstToolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = firstToolRequest.CallId,
                Content = "result a",
                TokensUsed = 10
            }
        });
        await call.RequestStream.CompleteAsync();

        var completion = (await ReadNextServerMessageAsync(call)).Completion;
        await Assert.That(completion.Content).IsEqualTo("degenerate stopped");
        await Assert.That(completion.StopReason).IsEqualTo(StopReason.ToolLimit);
    }

    [Test]
    public async Task CompleteWithTools_ReturnsInternalWhenGrokFailsMidLoop()
    {
        var grokClient = new ScriptedGrokClient(
            static (_, _) => Task.FromResult(ToolCallResult(
                [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":20}")])),
            static (_, _) => throw new GrokApiException("boom", StatusCode.Internal));

        await using var server = await InferenceTestHost.StartAsync(grokClient, []);
        var client = server.CreateClient();

        using var call = client.CompleteWithTools();
        await call.RequestStream.WriteAsync(new ClientMessage { Request = CreateToolLoopRequest() });

        var toolRequest = (await ReadNextServerMessageAsync(call)).ToolRequest;
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            ToolResponse = new ToolResponse
            {
                CallId = toolRequest.CallId,
                Content = "result a",
                TokensUsed = 10
            }
        });

        var exception = await Assert.That(async () => await call.ResponseStream.MoveNext(CancellationToken.None))
            .Throws<RpcException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Internal);
    }

    private static async Task InvokeStreamingAsync(InferenceService.InferenceServiceClient client, Metadata? headers)
    {
        using var call = client.CompleteWithTools(headers);
        await call.RequestStream.WriteAsync(new ClientMessage
        {
            Request = new CompleteRequest { Prompt = "stream prompt" }
        });
        await call.RequestStream.CompleteAsync();
        await call.ResponseStream.MoveNext(CancellationToken.None);
    }

    private static async Task<ServerMessage> ReadNextServerMessageAsync(AsyncDuplexStreamingCall<ClientMessage, ServerMessage> call)
    {
        var hasNext = await call.ResponseStream.MoveNext(CancellationToken.None);
        if (!hasNext)
            throw new InvalidOperationException("Expected a server message but the stream completed");

        return call.ResponseStream.Current;
    }

    private static CompleteRequest CreateToolLoopRequest(
        int toolTokenBudget = 100,
        int maxRounds = 0,
        Effort effort = Effort.Balanced,
        string prompt = "Explain the code")
    {
        var request = new CompleteRequest
        {
            Prompt = prompt,
            Context = "explore context",
            Effort = effort,
            ToolTokenBudget = toolTokenBudget,
            MaxRounds = maxRounds
        };
        request.Tools.Add(CreateReadTool());
        return request;
    }

    private static ToolDefinition CreateReadTool()
    {
        return new ToolDefinition
        {
            Name = "read",
            Description = "Read a file",
            ParametersJson = "{\"type\":\"object\"}"
        };
    }

    private static GrokCompletionResult CompletionResult(
        string content,
        string reasoning = "",
        StopReason stopReason = StopReason.Stop,
        int inputTokens = 0,
        int outputTokens = 0,
        int thinkingTokens = 0,
        string model = "grok-4-1-fast-non-reasoning")
    {
        return new GrokCompletionResult(
            content,
            reasoning,
            stopReason,
            new Usage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ThinkingTokens = thinkingTokens
            },
            model,
            [],
            null);
    }

    private static GrokCompletionResult ToolCallResult(
        IReadOnlyList<GrokFunctionCall> toolCalls,
        int inputTokens = 0,
        int outputTokens = 0,
        int thinkingTokens = 0,
        string? encryptedContent = null)
    {
        return new GrokCompletionResult(
            "",
            "",
            StopReason.Stop,
            new Usage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ThinkingTokens = thinkingTokens
            },
            "grok-4-1-fast-non-reasoning",
            toolCalls,
            encryptedContent);
    }

    private static string ComputeHash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private sealed class InferenceTestHost : IAsyncDisposable
    {
        private IHost Host { get; init; } = null!;

        private GrpcChannel? _channel;

        public static async Task<InferenceTestHost> StartAsync(
            IGrokClient grokClient,
            string[] hashes,
            Action<InferenceServiceOptions>? configureOptions = null)
        {
            var builder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder();
            builder.ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddGrpc(options => options.Interceptors.Add<AuthInterceptor>());
                    services.AddHttpClient(nameof(AuthValidationService));
                    services.AddSingleton<AuthInterceptor>();
                    services.AddSingleton<AuthValidationService>();
                    services.AddSingleton<IHostedService, JwksWarmupHostedService>();
                    services.Configure<AuthOptions>(options =>
                    {
                        options.ApiKeyHashes = hashes.Length == 0 ? [ComputeHash(TestAuthToken)] : hashes;
                        options.JwksUri = string.Empty;
                        options.ClientId = string.Empty;
                        options.Issuer = string.Empty;
                    });
                    services.Configure<InferenceServiceOptions>(options =>
                    {
                        options.GrokApiKey = "test-grok-key";
                        configureOptions?.Invoke(options);
                    });
                    services.AddSingleton(grokClient);
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapGrpcService<InferenceServiceImpl>());
                });
            });

            var host = builder.Build();
            await host.StartAsync();

            return new InferenceTestHost { Host = host };
        }

        public InferenceService.InferenceServiceClient CreateClient(bool authenticated = true)
        {
            var server = Host.GetTestServer();
            var httpClient = server.CreateClient();
            if (authenticated)
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAuthToken);
            _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpClient = httpClient
            });

            return new InferenceService.InferenceServiceClient(_channel);
        }

        public async ValueTask DisposeAsync()
        {
            if (_channel is not null)
                _channel.Dispose();

            await Host.StopAsync();
            Host.Dispose();
        }
    }

    private sealed class ScriptedGrokClient : IGrokClient
    {
        private readonly Queue<Func<GrokCompletionRequest, CancellationToken, Task<GrokCompletionResult>>> _steps;

        public ScriptedGrokClient(params Func<GrokCompletionRequest, CancellationToken, Task<GrokCompletionResult>>[] steps)
        {
            _steps = new Queue<Func<GrokCompletionRequest, CancellationToken, Task<GrokCompletionResult>>>(steps);
        }

        public List<GrokCompletionRequest> Requests { get; } = [];

        public Task<GrokCompletionResult> CompleteAsync(GrokCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (_steps.Count == 0)
                throw new InvalidOperationException("No scripted Grok response remains for this test");

            return _steps.Dequeue()(request, cancellationToken);
        }
    }
}
