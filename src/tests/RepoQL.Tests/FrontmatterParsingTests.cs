using System.Reflection;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Contracts.Models;
using RepoQL.Formats.Markdown;
using RepoQL.Testing.Scaffolding;

namespace RepoQL.Tests;

internal class FrontmatterParsingTests
{
    [Test]
    public async Task MarkdownFrontmatter_IsFlattenedIntoDocumentProps()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            ConfigureFormats(options);
        });

        var content = """
        ---
        description: Test document
        documentationCategory: example
        tags: [markdown, md, text/markdown]
        ---

        # Title
        """;

        var uri = repo.AddOrUpdateText("docs/fm.md", content);

        await repo.IndexAsync();

        Node? doc = repo.Store.GetDocumentByUri(uri);
        if (doc is null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            do
            {
                await Task.Delay(50);
                doc = repo.Store.GetDocumentByUri(uri);
                if (doc is not null)
                    break;
            } while (DateTime.UtcNow < deadline);
        }

        doc.Should().NotBeNull();
        doc!.Props!["description"]!.GetValue<string>().Should().Be("Test document");
        doc.Props!["documentationCategory"]!.GetValue<string>().Should().Be("example");
        var tags = doc.Props!["tags"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        tags.Should().BeEquivalentTo(["markdown", "md", "text/markdown"]);
    }

    [Test]
    public async Task MarkdownFrontmatter_IsPersistedAsJson()
    {
        await using var repo = await IndexedRepoBuilder.CreateAsync(options =>
        {
            options.EnableWatching = false;
            options.RunFullScanOnStartup = false;
            ConfigureFormats(options);
        });

        var content = """
        ---
        description: Frontmatter json preservation
        owner:
          name: RepoQL
          email: repoql@example.com
        labels:
          - indexing
          - markdown
        priority: 2
        ---

        # Title
        """;

        var uri = repo.AddOrUpdateText("docs/frontmatter.md", content);
        await repo.IndexAsync();

        var doc = await repo.WaitForDocumentAsync(uri, TimeSpan.FromSeconds(5));
        doc.Should().NotBeNull();
        doc!.Props!.TryGetPropertyValue("frontmatter", out var fmNode).Should().BeTrue();
        fmNode.Should().NotBeNull();

        var fm = fmNode!.AsObject();
        fm["description"]!.GetValue<string>().Should().Be("Frontmatter json preservation");
        var priorityNode = fm["priority"]!;
        priorityNode.GetValue<long>().Should().Be(2);

        var owner = fm["owner"]!.AsObject();
        owner["name"]!.GetValue<string>().Should().Be("RepoQL");
        owner["email"]!.GetValue<string>().Should().Be("repoql@example.com");

        var labels = fm["labels"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        labels.Should().BeEquivalentTo(["indexing", "markdown"]);
    }

    [Test]
    public void YamlFrontmatter_ScalarTypesPreserved()
    {
        var loaderType = typeof(MarkdownLoader);
        var method = loaderType
            .GetMethod("YamlToJson", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Unable to locate MarkdownLoader.YamlToJson");

        var yaml = """
        priority: 2
        description: hello
        """;

        var node = (JsonNode?)method.Invoke(null, new object?[] { yaml });
        node.Should().NotBeNull();

        var json = node!.AsObject();
        json["priority"]!.GetValue<long>().Should().Be(2);
        json["description"]!.GetValue<string>().Should().Be("hello");
    }

    private static void ConfigureFormats(IndexedRepoOptions options)
    {
        options.AddMarkdownFormat();
        options.AddMermaidFormat();
    }
}
