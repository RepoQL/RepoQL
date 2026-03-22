using System.Text.Json;
using AwesomeAssertions;
using ModelContextProtocol.Protocol;

namespace RepoQL.Mcp.Client.Tests;

public class McpClientRegistryResultSerializationTests
{
    [Test]
    public async Task SerializeToolResult_WithStructuredContent_PrefersStructuredJson()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonDocument.Parse("""
                {"data":{"actor":{"accounts":[{"id":1418123,"name":"Church Community Builder"}]}}}
                """).RootElement,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "ignored text payload" }
            }
        };

        var serialized = McpClientRegistry.SerializeToolResult(result);

        serialized.Should().Contain("\"accounts\"");
        serialized.Should().Contain("\"Church Community Builder\"");
        serialized.Should().NotContain("ignored text payload");
    }

    [Test]
    public async Task SerializeToolResult_WithSingleTextBlock_ReturnsRawText()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = """{"status":"ok"}""" }
            }
        };

        var serialized = McpClientRegistry.SerializeToolResult(result);

        serialized.Should().Be("""{"status":"ok"}""");
    }

    [Test]
    public async Task SerializeToolResult_WithNonTextContent_SerializesJsonNotToString()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new ImageContentBlock
                {
                    MimeType = "image/png",
                    Data = Convert.FromBase64String("AA==")
                }
            }
        };

        var serialized = McpClientRegistry.SerializeToolResult(result);

        serialized.Should().Contain("\"type\"");
        serialized.Should().Contain("image");
        serialized.Should().NotContain("ModelContextProtocol.Protocol");
    }

    [Test]
    public async Task SerializeToolResult_WithNoContent_ReturnsNullLiteral()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>()
        };

        var serialized = McpClientRegistry.SerializeToolResult(result);

        serialized.Should().Be("null");
    }
}
