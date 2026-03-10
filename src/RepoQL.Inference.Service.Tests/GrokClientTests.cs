using FakeItEasy;
using Grpc.Core;
using XaiApi;

namespace RepoQL.Inference.Service.Tests;

public sealed class GrokClientTests
{
    [Test]
    public async Task CompleteAsync_MapsConversationUsageAndTools()
    {
        var chatClient = A.Fake<IXaiChatClient>();
        var options = Microsoft.Extensions.Options.Options.Create(new InferenceServiceOptions
        {
            GrokApiKey = "grok-secret"
        });
        var client = new GrokClient(chatClient, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<GrokClient>.Instance);

        GetCompletionsRequest? capturedRequest = null;
        Metadata? capturedHeaders = null;
        A.CallTo(() => chatClient.GetCompletionAsync(
                A<GetCompletionsRequest>.Ignored,
                A<Metadata>.Ignored,
                A<DateTime>.Ignored,
                A<CancellationToken>.Ignored))
            .Invokes(call =>
            {
                capturedRequest = call.GetArgument<GetCompletionsRequest>(0);
                capturedHeaders = call.GetArgument<Metadata>(1);
            })
            .Returns(new GetChatCompletionResponse
            {
                Choices =
                {
                    new CompletionOutput
                    {
                        Message = new Message
                        {
                            Content = { new Content { Text = "answer" } },
                            ReasoningContent = { new Content { Text = "reasoning" } },
                            EncryptedContent = "ciphertext"
                        },
                        FinishReason = FinishReason.Stop
                    }
                },
                Usage = new XaiApi.Usage
                {
                    PromptTokens = 12,
                    CompletionTokens = 7,
                    ReasoningTokens = 3
                }
            });

        var result = await client.CompleteAsync(
            new GrokCompletionRequest(
                [
                    new GrokMessage(GrokMessageRole.Developer, "system prompt"),
                    new GrokMessage(GrokMessageRole.User, "context block"),
                    new GrokMessage(
                        GrokMessageRole.Assistant,
                        "tool call",
                        [new GrokFunctionCall("call-1", "read", "{\"uri\":\"file:///a\",\"tokenBudget\":50}")],
                        Reasoning: "assistant reasoning",
                        EncryptedContent: "previous-cipher"),
                    new GrokMessage(GrokMessageRole.Tool, "tool result", ToolCallId: "call-1"),
                    new GrokMessage(GrokMessageRole.User, "user question")
                ],
                Effort.High,
                123,
                [
                    new ToolDefinition
                    {
                        Name = "read",
                        Description = "Read a file",
                        ParametersJson = "{\"type\":\"object\"}"
                    }
                ],
                GrokToolMode.Auto,
                ParallelToolCalls: true,
                IncludeEncryptedReasoningContent: true,
                RoundNumber: 2),
            CancellationToken.None);

        await Assert.That(capturedRequest).IsNotNull();
        await Assert.That(capturedRequest!.Model).IsEqualTo("grok-4-1-fast-reasoning");
        await Assert.That(capturedRequest.MaxTokens).IsEqualTo(123);
        await Assert.That(capturedRequest.Messages.Count).IsEqualTo(5);
        await Assert.That(capturedRequest.Messages[0].Role).IsEqualTo(MessageRole.Developer);
        await Assert.That(capturedRequest.Messages[2].Role).IsEqualTo(MessageRole.Assistant);
        await Assert.That(capturedRequest.Messages[2].FunctionCalls[0].Name).IsEqualTo("read");
        await Assert.That(capturedRequest.Messages[2].EncryptedContent).IsEqualTo("previous-cipher");
        await Assert.That(capturedRequest.Messages[3].Role).IsEqualTo(MessageRole.Tool);
        await Assert.That(capturedRequest.Messages[3].ToolCallId).IsEqualTo("call-1");
        await Assert.That(capturedRequest.Tools.Count).IsEqualTo(1);
        await Assert.That(capturedRequest.ToolChoice!.Mode).IsEqualTo(ToolMode.Auto);
        await Assert.That(capturedRequest.Include).Contains("reasoning.encrypted_content");
        await Assert.That(capturedHeaders!.GetValue("authorization")).IsEqualTo("Bearer grok-secret");

        await Assert.That(result.Content).IsEqualTo("answer");
        await Assert.That(result.Reasoning).IsEqualTo("reasoning");
        await Assert.That(result.StopReason).IsEqualTo(StopReason.Stop);
        await Assert.That(result.Model).IsEqualTo("grok-4-1-fast-reasoning");
        await Assert.That(result.EncryptedContent).IsEqualTo("ciphertext");
        await Assert.That(result.Usage.InputTokens).IsEqualTo(12);
        await Assert.That(result.Usage.OutputTokens).IsEqualTo(7);
        await Assert.That(result.Usage.ThinkingTokens).IsEqualTo(3);
    }

    [Test]
    public async Task CompleteAsync_MapsFunctionCallsFromProvider()
    {
        var chatClient = A.Fake<IXaiChatClient>();
        var options = Microsoft.Extensions.Options.Options.Create(new InferenceServiceOptions
        {
            GrokApiKey = "grok-secret"
        });
        var client = new GrokClient(chatClient, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<GrokClient>.Instance);

        A.CallTo(() => chatClient.GetCompletionAsync(
                A<GetCompletionsRequest>.Ignored,
                A<Metadata>.Ignored,
                A<DateTime>.Ignored,
                A<CancellationToken>.Ignored))
            .Returns(new GetChatCompletionResponse
            {
                Choices =
                {
                    new CompletionOutput
                    {
                        Message = new Message
                        {
                            FunctionCalls =
                            {
                                new FunctionCall
                                {
                                    Id = "call-1",
                                    Name = "read",
                                    Arguments = "{\"uri\":\"file:///src/a.cs\",\"tokenBudget\":200}"
                                }
                            }
                        },
                        FinishReason = FinishReason.ToolCalls
                    }
                },
                Usage = new XaiApi.Usage()
            });

        var result = await client.CompleteAsync(
            new GrokCompletionRequest(
                [new GrokMessage(GrokMessageRole.User, "prompt")],
                Effort.Balanced,
                null,
                []),
            CancellationToken.None);

        await Assert.That(result.ToolCalls.Count).IsEqualTo(1);
        await Assert.That(result.ToolCalls[0].Id).IsEqualTo("call-1");
        await Assert.That(result.ToolCalls[0].Name).IsEqualTo("read");
        await Assert.That(result.ToolCalls[0].ArgumentsJson).Contains("\"tokenBudget\":200");
    }

    [Test]
    public async Task CompleteAsync_MapsProviderRateLimitToTypedException()
    {
        var chatClient = A.Fake<IXaiChatClient>();
        var options = Microsoft.Extensions.Options.Options.Create(new InferenceServiceOptions
        {
            GrokApiKey = "grok-secret"
        });
        var client = new GrokClient(chatClient, options, Microsoft.Extensions.Logging.Abstractions.NullLogger<GrokClient>.Instance);

        A.CallTo(() => chatClient.GetCompletionAsync(
                A<GetCompletionsRequest>.Ignored,
                A<Metadata>.Ignored,
                A<DateTime>.Ignored,
                A<CancellationToken>.Ignored))
            .Throws(new RpcException(new Status(StatusCode.ResourceExhausted, "rate limited")));

        var exception = await Assert.That(async () => await client.CompleteAsync(
                new GrokCompletionRequest(
                    [new GrokMessage(GrokMessageRole.User, "prompt")],
                    Effort.Balanced,
                    null,
                    []),
                CancellationToken.None))
            .Throws<GrokApiException>();

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.ResourceExhausted);
    }
}
