using AwesomeAssertions;

namespace RepoQL.LLM.Client.Tests;

public class LlmPromptTemplatesTests
{
    #region BuildSummarizePrompt

    [Test]
    public async Task BuildSummarizePrompt_IncludesIntent()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "test data",
            "Find authentication patterns",
            500);

        result.Should().Contain("Find authentication patterns");
    }

    [Test]
    public async Task BuildSummarizePrompt_IncludesData()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "[1]{uri}:\nfile:///test.cs",
            "intent",
            500);

        result.Should().Contain("[1]{uri}:");
        result.Should().Contain("file:///test.cs");
    }

    [Test]
    public async Task BuildSummarizePrompt_IncludesTokenLimit()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "data",
            "intent",
            300);

        result.Should().Contain("300");
    }

    [Test]
    public async Task BuildSummarizePrompt_MentionsToonFormat()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "data",
            "intent",
            500);

        result.Should().Contain("TOON");
    }

    #endregion

    #region BuildExtractPrompt

    [Test]
    public async Task BuildExtractPrompt_IncludesIntent()
    {
        var result = LlmPromptTemplates.BuildExtractPrompt(
            "test data",
            "How does authentication work?");

        result.Should().Contain("How does authentication work?");
    }

    [Test]
    public async Task BuildExtractPrompt_IncludesData()
    {
        var result = LlmPromptTemplates.BuildExtractPrompt(
            "[2]{uri,headline}:\ntest1\ntest2",
            "intent");

        result.Should().Contain("[2]{uri,headline}:");
    }

    [Test]
    public async Task BuildExtractPrompt_MentionsMarkdownOutput()
    {
        var result = LlmPromptTemplates.BuildExtractPrompt(
            "data",
            "intent");

        // Should produce a markdown report from the data
        result.Should().Contain("markdown report");
    }

    [Test]
    public async Task BuildExtractPrompt_DescribesOutputFormat()
    {
        var result = LlmPromptTemplates.BuildExtractPrompt(
            "data",
            "intent");

        // Should describe the expected output format with URIs and code blocks
        result.Should().Contain("<uri>");
        result.Should().Contain("synthesis");
    }

    #endregion
}
