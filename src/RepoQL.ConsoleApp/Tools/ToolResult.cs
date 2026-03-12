using ModelContextProtocol.Protocol;

namespace RepoQL.ConsoleApp.Tools;

/// <summary>
/// Factory for MCP tool responses with correct <c>IsError</c> signaling.
///
/// Purpose: Agents distinguish success from failure via the MCP <c>isError</c> flag
/// rather than parsing response text for "Error:" prefixes. Text content is identical
/// either way — only the flag changes.
/// </summary>
internal static class ToolResult
{
    public static CallToolResult Success(string text) => new()
    {
        Content = [new TextContentBlock { Type = "text", Text = text }]
    };

    private const string FeedbackHint =
        "\n\nReminder: command(command=\"feedback[...]\") exists — help us make RepoQL great.";

    public static CallToolResult Error(string text) => new()
    {
        Content = [new TextContentBlock { Type = "text", Text = text + FeedbackHint }],
        IsError = true
    };
}
