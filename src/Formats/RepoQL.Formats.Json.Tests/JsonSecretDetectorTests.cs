using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Formats.Json.Analysis;

namespace RepoQL.Formats.Json.Tests;

public sealed class JsonSecretDetectorTests
{
    [Test]
    [DisplayName("Key named password with non-empty value produces one annotation")]
    public async Task AnalyzeAsync_PasswordKeyWithValue_ProducesAnnotation()
    {
        const string json = """
        {
          "password": "super-secret-value"
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().HaveCount(1);
        results[0].Message.Should().Contain("/password");
    }

    [Test]
    [DisplayName("Key named api_key with sk-prefixed value produces annotation")]
    public async Task AnalyzeAsync_ApiKeyWithSkPrefix_ProducesAnnotation()
    {
        const string json = """
        {
          "api_key": "sk-abc123def456"
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().HaveCount(1);
        GetPath(results[0]).Should().Be("/api_key");
    }

    [Test]
    [DisplayName("Value starting with ghp_ produces annotation regardless of key name")]
    public async Task AnalyzeAsync_GhpPrefixValue_ProducesAnnotation()
    {
        const string json = """
        {
          "description": "ghp_abc123def456ghi789"
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().HaveCount(1);
        GetPath(results[0]).Should().Be("/description");
    }

    [Test]
    [DisplayName("Safe description value produces no annotation")]
    public async Task AnalyzeAsync_DescriptionWithSafeValue_ProducesNoAnnotation()
    {
        const string json = """
        {
          "description": "Service configuration for local development"
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Password key with empty value produces no annotation")]
    public async Task AnalyzeAsync_PasswordKeyWithEmptyValue_ProducesNoAnnotation()
    {
        const string json = """
        {
          "password": ""
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Password key with placeholder value produces no annotation")]
    public async Task AnalyzeAsync_PasswordKeyWithPlaceholder_ProducesNoAnnotation()
    {
        const string json = """
        {
          "password": "<your-password-here>"
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().BeEmpty();
    }

    [Test]
    [DisplayName("File with no secret-like content produces zero annotations")]
    public async Task AnalyzeAsync_NoSecretContent_ProducesNoAnnotations()
    {
        const string json = """
        {
          "name": "repoql",
          "description": "queryable repository index",
          "enabled": true
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Annotations use key start line values")]
    public async Task AnalyzeAsync_AnnotationsContainExpectedStartLines()
    {
        const string json = """
        {
          "password": "alpha-secret-value",
          "settings": {
            "description": "safe",
            "tokenValue": "ghp_abc123def456ghi789"
          }
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().HaveCount(2);

        var linesByPath = results.ToDictionary(GetPath, GetStartLine);
        linesByPath["/password"].Should().Be(2);
        linesByPath["/settings/tokenValue"].Should().Be(5);
    }

    [Test]
    [DisplayName("All annotations use json.potential-secret rule id and warning severity")]
    public async Task AnalyzeAsync_AnnotationsUseExpectedRuleAndSeverity()
    {
        const string json = """
        {
          "password": "alpha-secret-value",
          "notes": "ghp_abc123def456ghi789",
          "api_key": "sk-abc123def456"
        }
        """;

        var results = await AnalyzeAsync(json);

        results.Should().HaveCount(3);
        results.Should().OnlyContain(r =>
            r.RuleId == "json.potential-secret"
            && r.Severity == AnalysisSeverity.Warning);
    }

    private static async Task<IReadOnlyList<AnalysisResult>> AnalyzeAsync(string json)
    {
        var parser = new JsonStructureParser();
        var parseResult = parser.Parse(json);

        var metadata = new Dictionary<string, object?>
        {
            [JsonLoader.StateMetadataKey] = parseResult
        };

        var document = new DocumentModel(
            RepoUri.Parse("file:///settings.json"),
            JsonMediaTypes.Json,
            json,
            metadata: metadata);

        var analyzer = new JsonSecretDetector(NullLogger<JsonSecretDetector>.Instance);
        var context = new AnalyzerContext(new AnalyzerSettings(), "C:\\repo");

        var results = new List<AnalysisResult>();
        await foreach (var result in analyzer.AnalyzeAsync(document, context, CancellationToken.None))
        {
            results.Add(result);
        }

        return results;
    }

    private static string GetPath(AnalysisResult result)
        => result.Data?["path"]?.ToString() ?? string.Empty;

    private static int? GetStartLine(AnalysisResult result)
        => result.Target?.TargetUri?.Loc.Line?.Start;
}
