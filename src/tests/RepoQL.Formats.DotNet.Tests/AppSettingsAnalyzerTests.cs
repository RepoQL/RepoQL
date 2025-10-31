using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;
using RepoQL.Core;

namespace RepoQL.Formats.DotNet.Tests;

internal class AppSettingsAnalyzerTests
{
    [Test]
    public async Task AnalyzeAsync_DetectsPotentialSecrets()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var analyzer = new AppSettingsAnalyzer();

        var json = """
        {
          "Authentication": {
            "JwtSecret": "MyHardcodedSecret123",
            "ApiKey": "sk-live-abc123"
          }
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);
            var context = CreateAnalyzerContext();

            // Act
            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context))
            {
                results.Add(result);
            }

            // Assert
            results.Should().HaveCount(2);
            results.Should().OnlyContain(r => r.RuleId == "config/potential-secret");
            results.Should().Contain(r => r.Message.Contains("Authentication:JwtSecret"));
            results.Should().Contain(r => r.Message.Contains("Authentication:ApiKey"));
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task AnalyzeAsync_ErrorsOnProductionSecretsAsync()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var analyzer = new AppSettingsAnalyzer();

        var json = """
        {
          "Authentication": {
            "ClientSecret": "prod-secret-123"
          }
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.Production.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);
            var context = CreateAnalyzerContext();

            // Act
            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context))
            {
                results.Add(result);
            }

            // Assert
            var productionError = results.FirstOrDefault(r => r.RuleId == "config/production-secrets");
            productionError.Should().NotBeNull();
            productionError!.Severity.Should().Be(AnalysisSeverity.Error);
            productionError.Message.Should().Contain("Production configuration contains");
            productionError.Message.Should().Contain("hardcoded secret");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task AnalyzeAsync_SkipsPlaceholders()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var analyzer = new AppSettingsAnalyzer();

        var json = """
        {
          "Authentication": {
            "JwtSecret": "${JWT_SECRET}",
            "ApiKey": "your-api-key-here",
            "ClientSecret": "<add-secret-here>"
          }
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);
            var context = CreateAnalyzerContext();

            // Act
            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context))
            {
                results.Add(result);
            }

            // Assert
            results.Should().BeEmpty("placeholders should not be flagged as secrets");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task AnalyzeAsync_RespectsDisabledRules()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var analyzer = new AppSettingsAnalyzer();

        var json = """
        {
          "Authentication": {
            "ApiKey": "hardcoded-key"
          }
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);
            var context = CreateAnalyzerContext(rules =>
            {
                // Disable the rule
                rules["config/potential-secret"] = new AnalyzerRuleSettings
                {
                    RuleId = "config/potential-secret",
                    Severity = AnalysisSeverity.None
                };
            });

            // Act
            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context))
            {
                results.Add(result);
            }

            // Assert
            results.Should().BeEmpty("rule should be disabled");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public async Task AnalyzeAsync_IncludesHelpLinks()
    {
        // Arrange
        var loader = new AppSettingsLoader();
        var analyzer = new AppSettingsAnalyzer();

        var json = """
        {
          "Authentication": {
            "Secret": "hardcoded"
          }
        }
        """;

        var (artifact, tempPath) = CreateArtifact("appsettings.json", json);

        try
        {
            var document = await loader.LoadAsync(artifact);
            var context = CreateAnalyzerContext();

            // Act
            var results = new List<AnalysisResult>();
            await foreach (var result in analyzer.AnalyzeAsync(document, context))
            {
                results.Add(result);
            }

            // Assert
            var secretResult = results.First(r => r.RuleId == "config/potential-secret");
            secretResult.Data.Should().NotBeNull();
            secretResult.Data!["help"]?.ToString().Should().Contain("https://");
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    [Test]
    public void Supports_ReturnsTrueForAppSettingsMediaType()
    {
        // Arrange
        var analyzer = new AppSettingsAnalyzer();
        var mediaType = SemanticMediaType.Create("application", "json").WithKind("config.appsettings");

        // Act
        var supports = analyzer.Supports(mediaType);

        // Assert
        supports.Should().BeTrue();
    }

    [Test]
    public void Supports_ReturnsFalseForOtherMediaTypes()
    {
        // Arrange
        var analyzer = new AppSettingsAnalyzer();
        var mediaType = SemanticMediaType.Create("text", "markdown").WithKind("markdown.doc");

        // Act
        var supports = analyzer.Supports(mediaType);

        // Assert
        supports.Should().BeFalse();
    }

    private static (DiscoveredArtifact artifact, string tempPath) CreateArtifact(string fileName, string content)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"repoql_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, fileName);
        File.WriteAllText(tempPath, content, Encoding.UTF8);

        var provider = new PhysicalFileProvider(tempDir);

        return (new DiscoveredArtifact
        {
            File = provider.GetFileInfo(fileName),
            RepoUri = RepoUri.Parse($"file:///{fileName}")
        }, tempPath);
    }

    private static AnalyzerContext CreateAnalyzerContext(Action<Dictionary<string, AnalyzerRuleSettings>>? configure = null)
    {
        var rules = new Dictionary<string, AnalyzerRuleSettings>
        {
            ["config/potential-secret"] = new AnalyzerRuleSettings
            {
                RuleId = "config/potential-secret",
                Severity = AnalysisSeverity.Warning
            },
            ["config/production-secrets"] = new AnalyzerRuleSettings
            {
                RuleId = "config/production-secrets",
                Severity = AnalysisSeverity.Error
            },
            ["config/missing-connection-strings"] = new AnalyzerRuleSettings
            {
                RuleId = "config/missing-connection-strings",
                Severity = AnalysisSeverity.Warning
            }
        };

        configure?.Invoke(rules);

        var settings = new AnalyzerSettings(rules);
        var formatRegistry = new FormatRegistry(Array.Empty<FormatDescriptor>());

        return new AnalyzerContext(
            settings,
            repositoryPath: "C:\\test",
            formatRegistry: formatRegistry,
            workspace: new TestWorkspace());
    }

    private class TestWorkspace : IAnalysisWorkspace
    {
        public Task<DocumentModel?> LoadAsync(RepoUri uri, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<DocumentModel?>(null);
        }

        public Task<IReadOnlyList<EmbeddedFragment>> DiscoverEmbedsAsync(DocumentModel document, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<EmbeddedFragment>>(Array.Empty<EmbeddedFragment>());
        }
    }

    private static void Cleanup(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
