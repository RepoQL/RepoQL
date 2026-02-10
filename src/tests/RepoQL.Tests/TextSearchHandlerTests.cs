using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using RepoQL.ConsoleApp.Host;
using RepoQL.Contracts;
using RepoQL.Contracts.Embeddings;
using RepoQL.Data.DuckDB;
using RepoQL.Explore;

namespace RepoQL.Tests;

internal sealed class TextSearchHandlerTests
{
    [Test]
    [DisplayName("TextSearchHandler returns no files matched for empty documents")]
    public async Task TextSearchHandler_NoDocuments_ReturnsNoFilesMatched()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync([], "token", 1000, CancellationToken.None);

        result.Content.Should().Be("No files matched.");
    }

    [Test]
    [DisplayName("TextSearchHandler returns usage when parameter is null")]
    public async Task TextSearchHandler_NullParameter_ReturnsUsage()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Foo.cs", "line1\nline2", "text/plain", null, null, null)
            ],
            null,
            1000,
            CancellationToken.None);

        result.Content.Should().Be("Usage: `=> grep: <search term>` or `=> regex: <pattern>`");
    }

    [Test]
    [DisplayName("TextSearchHandler returns usage when parameter is empty")]
    public async Task TextSearchHandler_EmptyParameter_ReturnsUsage()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Foo.cs", "line1\nline2", "text/plain", null, null, null)
            ],
            "   ",
            1000,
            CancellationToken.None);

        result.Content.Should().Be("Usage: `=> grep: <search term>` or `=> regex: <pattern>`");
    }

    [Test]
    [DisplayName("TextSearchHandler grep mode matches case-insensitively")]
    public async Task TextSearchHandler_Grep_CaseInsensitiveMatch()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Auth.cs", "validateToken\nother", "text/plain", null, null, null)
            ],
            "VALIDATETOKEN",
            5000,
            CancellationToken.None);

        result.Content.Should().Contain("file:///src/Auth.cs#line=1");
        result.Content.Should().Contain("   1: validateToken");
    }

    [Test]
    [DisplayName("TextSearchHandler grep mode returns no match message")]
    public async Task TextSearchHandler_Grep_NoMatches()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Auth.cs", "abc\ndef", "text/plain", null, null, null)
            ],
            "xyz",
            2000,
            CancellationToken.None);

        result.Content.Should().Be("No matches for 'xyz' in 1 files.");
    }

    [Test]
    [DisplayName("TextSearchHandler grep mode includes line fragments across files")]
    public async Task TextSearchHandler_Grep_MultipleFiles_IncludeLineFragments()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/A.cs", "x\nneedle\ny", "text/plain", null, null, null),
                new ReadDocument("file:///src/B.cs", "needle\nz", "text/plain", null, null, null)
            ],
            "needle",
            5000,
            CancellationToken.None);

        result.Content.Should().Contain("file:///src/A.cs#line=2");
        result.Content.Should().Contain("file:///src/B.cs#line=1");
    }

    [Test]
    [DisplayName("TextSearchHandler grep mode renders one line before and after context")]
    public async Task TextSearchHandler_Grep_IncludesContextLines()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Ctx.cs", "line1\ntarget\nline3", "text/plain", null, null, null)
            ],
            "target",
            5000,
            CancellationToken.None);

        result.Content.Should().Contain("   1: line1");
        result.Content.Should().Contain("   2: target");
        result.Content.Should().Contain("   3: line3");
    }

    [Test]
    [DisplayName("TextSearchHandler grep mode handles first and last line matches without out-of-range context")]
    public async Task TextSearchHandler_Grep_FirstAndLastLineContext()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Edge.cs", "match\nmiddle\ntail\nmatch", "text/plain", null, null, null)
            ],
            "match",
            5000,
            CancellationToken.None);

        result.Content.Should().Contain("file:///src/Edge.cs#line=1");
        result.Content.Should().Contain("file:///src/Edge.cs#line=4");
        result.Content.Should().NotContain("   0:");
        result.Content.Should().NotContain("   5:");
    }

    [Test]
    [DisplayName("TextSearchHandler regex mode matches patterns")]
    public async Task TextSearchHandler_Regex_MatchesPattern()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("regex").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Regex.cs", "abc\n123\nxyz", "text/plain", null, null, null)
            ],
            "\\d+",
            5000,
            CancellationToken.None);

        result.Content.Should().Contain("file:///src/Regex.cs#line=2");
        result.Content.Should().Contain("   2: 123");
    }

    [Test]
    [DisplayName("TextSearchHandler regex mode rejects invalid patterns")]
    public async Task TextSearchHandler_Regex_InvalidPattern_ReturnsError()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("regex").Should().BeTrue();

        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Regex.cs", "abc", "text/plain", null, null, null)
            ],
            "[abc",
            5000,
            CancellationToken.None);

        result.Content.Should().Contain("Invalid regex pattern:");
    }

    [Test]
    [DisplayName("TextSearchHandler truncates output to token budget with showing footer")]
    public async Task TextSearchHandler_BudgetTruncation_ShowsShowingFooter()
    {
        var handler = new TextSearchHandler();
        handler.CanHandle("grep").Should().BeTrue();

        var content = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"match line {i} with extra text"));
        var result = await handler.ExecuteAsync(
            [
                new ReadDocument("file:///src/Budget.cs", content, "text/plain", null, null, null)
            ],
            "match",
            50,
            CancellationToken.None);

        result.Content.Should().Contain("[showing ");
        result.Content.Should().Contain("/20 matches");
    }

    // === SQL Macro Integration Tests ===

    private static DuckDbDataStore CreateStoreWithDocuments()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new RepositoryConfiguration { Path = "/repo" });
        services.AddSingleton<UriRegistry>();
        services.AddSingleton<IEmbeddingProvider?>(sp => null);
        services.AddSingleton<ILlmProvider?>(sp => null);
        services.AddSingleton<IMcpToolCaller?>(sp => null);
        var provider = services.BuildServiceProvider();

        var store = new DuckDbDataStore(":memory:", serviceProvider: provider);

        var artId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var content = "public class TokenService\n{\n    public bool ValidateToken(string token)\n    {\n        return token != null;\n    }\n}";
        var escaped = content.Replace("'", "''");
        store.ExecuteRaw(
            $"INSERT INTO artifact (id, digest, byte_size, media_type, text_content, token_count) " +
            $"VALUES ('{artId}', 'abc', 100, 'text/plain', '{escaped}', 50)");
        store.ExecuteRaw(
            $"INSERT INTO node (id, kind, uri, container_uri_lowercase, artifact_id, properties, created_at, updated_at) " +
            $"VALUES ('{nodeId}', 'document', 'file:///src/TokenService.cs', 'file:///src/tokenservice.cs', '{artId}', " +
            "'{}'::JSON, NOW(), NOW())");

        return store;
    }

    [Test]
    [DisplayName("grep_matches SQL macro finds case-insensitive literal matches")]
    public void GrepMatches_Macro_FindsCaseInsensitiveMatches()
    {
        using var store = CreateStoreWithDocuments();

        var rows = store.Query("SELECT * FROM grep_matches('validatetoken')").ToList();

        rows.Should().HaveCount(1);
        rows[0]["uri"]?.ToString().Should().Be("file:///src/TokenService.cs");
        Convert.ToInt32(rows[0]["line_number"]).Should().Be(3);
        rows[0]["line_content"]?.ToString().Should().Contain("ValidateToken");
    }

    [Test]
    [DisplayName("grep_matches SQL macro respects scope filter")]
    public void GrepMatches_Macro_RespectsScope()
    {
        using var store = CreateStoreWithDocuments();

        var matches = store.Query("SELECT * FROM grep_matches('ValidateToken', 'file:///src/**/*.cs')").ToList();
        matches.Should().HaveCount(1);

        var noMatches = store.Query("SELECT * FROM grep_matches('ValidateToken', 'file:///lib/**')").ToList();
        noMatches.Should().HaveCount(0);
    }

    [Test]
    [DisplayName("regex_matches SQL macro finds pattern matches")]
    public void RegexMatches_Macro_FindsPatternMatches()
    {
        using var store = CreateStoreWithDocuments();

        var rows = store.Query(@"SELECT * FROM regex_matches('public\s+\w+\s+\w+\(')").ToList();

        rows.Should().HaveCount(1);
        rows[0]["line_content"]?.ToString().Should().Contain("ValidateToken");
    }

    [Test]
    [DisplayName("regex_matches SQL macro is case-sensitive by default")]
    public void RegexMatches_Macro_IsCaseSensitive()
    {
        using var store = CreateStoreWithDocuments();

        var upper = store.Query("SELECT * FROM regex_matches('ValidateToken')").ToList();
        upper.Should().HaveCount(1);

        var lower = store.Query("SELECT * FROM regex_matches('validatetoken')").ToList();
        lower.Should().HaveCount(0);
    }
}
