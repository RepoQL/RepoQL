using AwesomeAssertions;

namespace RepoQL.LLM.Client.Tests;

public class LlmPromptTemplatesTests
{
    #region BuildSummarizePrompt

    [Test]
    public void BuildSummarizePrompt_IncludesIntent()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "test data",
            "Find authentication patterns",
            500);

        result.User.Should().Contain("Find authentication patterns");
    }

    [Test]
    public void BuildSummarizePrompt_IncludesData()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "[1]{uri}:\nfile:///test.cs",
            "intent",
            500);

        result.User.Should().Contain("[1]{uri}:");
        result.User.Should().Contain("file:///test.cs");
    }

    [Test]
    public void BuildSummarizePrompt_IncludesTokenLimit()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "data",
            "intent",
            300);

        result.System.Should().Contain("300");
    }

    [Test]
    public void BuildSummarizePrompt_HasSystemPrompt()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "data",
            "intent",
            500);

        result.System.Should().NotBeNullOrEmpty();
        result.System.Should().Contain("Repository Analysis Agent");
    }

    #endregion

    #region BuildExtractPrompt

    [Test]
    public void BuildExtractPrompt_IncludesIntent()
    {
        var result = LlmPromptTemplates.BuildExtractPrompt(
            "test data",
            "How does authentication work?");

        result.User.Should().Contain("How does authentication work?");
    }

    [Test]
    public void BuildExtractPrompt_IncludesData()
    {
        var result = LlmPromptTemplates.BuildExtractPrompt(
            "[2]{uri,headline}:\ntest1\ntest2",
            "intent");

        result.User.Should().Contain("[2]{uri,headline}:");
    }

    [Test]
    public void BuildExtractPrompt_HasSystemPrompt()
    {
        var result = LlmPromptTemplates.BuildExtractPrompt(
            "data",
            "intent");

        result.System.Should().NotBeNullOrEmpty();
        result.System.Should().Contain("Repository Analysis Agent");
    }

    [Test]
    public void BuildSummarizePrompt_IncludesRepoTree_WhenProvided()
    {
        var repoTree = "src/\n  main.cs\n  helper.cs";
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "data",
            "intent",
            500,
            repoTree);

        result.User.Should().Contain("src/");
        result.User.Should().Contain("AvailableFiles");
    }

    [Test]
    public void BuildSummarizePrompt_OmitsRepoTree_WhenNull()
    {
        var result = LlmPromptTemplates.BuildSummarizePrompt(
            "data",
            "intent",
            500,
            repoTree: null);

        result.User.Should().NotContain("AvailableFiles");
    }

    #endregion
}
