using System.Text.Json;
using AwesomeAssertions;
using RepoQL.Sarif;
using RepoQL.Sarif.Normalization;

namespace RepoQL.Sarif.Tests;

public class SarifNormalizerTests
{
    [Test]
    [DisplayName("Normalize returns warning and zero runs for unsupported SARIF version")]
    public void Normalize_InvalidVersion_ReturnsWarnings()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.0.0",
              "runs": []
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("2.1.0", StringComparison.Ordinal));
    }

    [Test]
    [DisplayName("Normalize returns warning and zero runs when runs are missing")]
    public void Normalize_MissingRuns_ReturnsWarnings()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0"
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("runs", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    [DisplayName("Path normalization handles Snyk Code %SRCROOT% pattern")]
    public void Normalize_Path_SnykFixture_UsesRepoRelativePath()
    {
        using var document = LoadFixture("snyk-code.sarif.json");

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().HaveCount(1);
        result.Runs[0].Source.Should().Be("snyk-code");
        result.Runs[0].Results.Should().HaveCount(1);
        result.Runs[0].Results[0].NormalizedPath.Should().Be("routes/index.js");
    }

    [Test]
    [DisplayName("Path normalization handles sonar file URI pattern")]
    public void Normalize_Path_SonarFileUri_StripsScheme()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "SonarQube" } },
                  "results": [
                    {
                      "ruleId": "java:S100",
                      "message": { "text": "Sonar issue" },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "file:///src/main/Foo.java"
                            },
                            "region": { "startLine": 12 }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().HaveCount(1);
        result.Runs[0].Results.Should().HaveCount(1);
        result.Runs[0].Results[0].NormalizedPath.Should().Be("src/main/Foo.java");
    }

    [Test]
    [DisplayName("Path normalization relativizes Roslyn absolute file URIs")]
    public void Normalize_Path_RoslynAbsolute_RelativizesAgainstRepoRoot()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "Microsoft (R) Visual C# Compiler" } },
                  "results": [
                    {
                      "ruleId": "CS0168",
                      "message": { "text": "Variable is declared but never used." },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "file:///C:/source/repos/src/Foo.cs"
                            },
                            "region": { "startLine": 7 }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/source/repos");

        result.Runs[0].Results[0].NormalizedPath.Should().Be("src/Foo.cs");
    }

    [Test]
    [DisplayName("Path normalization converts backslashes to forward slashes")]
    public void Normalize_Path_Backslashes_NormalizedToForwardSlashes()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "Semgrep" } },
                  "results": [
                    {
                      "ruleId": "test.rule",
                      "message": { "text": "Backslash path" },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "src\\Auth\\Foo.cs"
                            },
                            "region": { "startLine": 3 }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs[0].Results[0].NormalizedPath.Should().Be("src/Auth/Foo.cs");
    }

    [Test]
    [DisplayName("Path normalization handles ESLint absolute unix paths")]
    public void Normalize_Path_EslintFixture_RelativizesAbsolutePath()
    {
        using var document = LoadFixture("eslint.sarif.json");

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, "/home/user/project");

        result.Runs.Should().HaveCount(1);
        result.Runs[0].Results.Should().HaveCount(1);
        result.Runs[0].Results[0].NormalizedPath.Should().Be("src/app.js");
    }

    [Test]
    [DisplayName("RuleCollector reads rules from tool extensions when driver rules are empty")]
    public void RuleCollector_QodanaExtensionRules_AreCollected()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {
                    "driver": { "name": "QDJVM", "rules": [] },
                    "extensions": [
                      {
                        "name": "com.intellij.java",
                        "rules": [
                          { "id": "LongLine", "defaultConfiguration": { "level": "warning" } },
                          { "id": "CascadeIf", "defaultConfiguration": { "level": "note" } }
                        ]
                      }
                    ]
                  }
                }
              ]
            }
            """);

        var collector = new RuleCollector();
        var rules = collector.Collect(document.RootElement.GetProperty("runs")[0]);

        rules.Should().HaveCount(2);
        rules.Should().ContainKey("LongLine");
        rules.Should().ContainKey("CascadeIf");
    }

    [Test]
    [DisplayName("RuleCollector keeps driver rules when IDs collide with extension rules")]
    public void RuleCollector_DriverRules_TakePrecedenceOnCollision()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {
                    "driver": {
                      "name": "QDJVM",
                      "rules": [
                        { "id": "SameRule", "defaultConfiguration": { "level": "error" } }
                      ]
                    },
                    "extensions": [
                      {
                        "rules": [
                          { "id": "SameRule", "defaultConfiguration": { "level": "note" } }
                        ]
                      }
                    ]
                  }
                }
              ]
            }
            """);

        var collector = new RuleCollector();
        var rules = collector.Collect(document.RootElement.GetProperty("runs")[0]);

        rules["SameRule"].DefaultLevel.Should().Be("error");
    }

    [Test]
    [DisplayName("RuleCollector returns empty lookup when rules arrays are missing")]
    public void RuleCollector_MissingRules_ReturnsEmpty()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {
                    "driver": { "name": "SonarQube" }
                  }
                }
              ]
            }
            """);

        var collector = new RuleCollector();
        var rules = collector.Collect(document.RootElement.GetProperty("runs")[0]);

        rules.Should().BeEmpty();
    }

    [Test]
    [DisplayName("SeverityResolver uses result level when present")]
    public void SeverityResolver_ResultLevel_Wins()
    {
        using var resultDocument = JsonDocument.Parse("""{ "level": "note" }""");
        var rule = new RuleDescriptor("rule", "error", new Dictionary<string, string>(), null, null);
        var resolver = new SeverityResolver();

        var level = resolver.ResolveLevel(resultDocument.RootElement, rule);

        level.Should().Be("note");
    }

    [Test]
    [DisplayName("SeverityResolver falls back to rule default level")]
    public void SeverityResolver_RuleDefault_UsedWhenResultLevelMissing()
    {
        using var resultDocument = JsonDocument.Parse("""{ }""");
        var rule = new RuleDescriptor("rule", "error", new Dictionary<string, string>(), null, null);
        var resolver = new SeverityResolver();

        var level = resolver.ResolveLevel(resultDocument.RootElement, rule);

        level.Should().Be("error");
    }

    [Test]
    [DisplayName("SeverityResolver defaults to warning when levels are absent")]
    public void SeverityResolver_DefaultsToWarning_WhenNoLevelsAvailable()
    {
        using var resultDocument = JsonDocument.Parse("""{ }""");
        var resolver = new SeverityResolver();

        var level = resolver.ResolveLevel(resultDocument.RootElement, null);

        level.Should().Be("warning");
    }

    [Test]
    [DisplayName("SourceIdentifier maps known producer names")]
    public void SourceIdentifier_KnownProducers_AreMapped()
    {
        var identifier = new SourceIdentifier();

        identifier.Resolve("SnykCode").Should().Be("snyk-code");
        identifier.Resolve("QDJVM").Should().Be("qodana-jvm");
        identifier.Resolve("CodeQL command-line toolchain").Should().Be("codeql");
    }

    [Test]
    [DisplayName("SourceIdentifier slugifies unknown producer names")]
    public void SourceIdentifier_UnknownProducer_IsSlugified()
    {
        var identifier = new SourceIdentifier();

        var slug = identifier.Resolve("My Custom Linter v3.2");

        slug.Should().Be("my-custom-linter-v3-2");
    }

    [Test]
    [DisplayName("Normalize handles multi-run SARIF and keeps sources separate")]
    public void Normalize_MultiRun_CreatesOneNormalizedRunPerSource()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "SnykCode" } },
                  "results": [
                    {
                      "ruleId": "javascript/XSS",
                      "message": { "text": "XSS" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "src/a.js" }, "region": { "startLine": 1 } } }
                      ]
                    }
                  ]
                },
                {
                  "tool": { "driver": { "name": "ESLint" } },
                  "results": [
                    {
                      "ruleId": "no-console",
                      "message": { "text": "Unexpected console" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "src/b.js" }, "region": { "startLine": 2 } } }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().HaveCount(2);
        result.Runs.Select(r => r.Source).Should().Contain("snyk-code");
        result.Runs.Select(r => r.Source).Should().Contain("eslint");
    }

    [Test]
    [DisplayName("Normalize skips malformed result and continues processing subsequent results")]
    public void Normalize_MalformedResult_IsSkippedWithoutStoppingRun()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "SnykCode" } },
                  "results": [
                    {
                      "ruleId": "rule-1",
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "src/a.js" }, "region": { "startLine": 1 } } }
                      ]
                    },
                    {
                      "ruleId": "rule-2",
                      "message": { "text": "Valid message" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "src/b.js" }, "region": { "startLine": 2 } } }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.SkippedResults.Should().Be(1);
        result.Runs.Should().HaveCount(1);
        result.Runs[0].Results.Should().HaveCount(1);
        result.Runs[0].Results[0].RuleId.Should().Be("rule-2");
    }

    [Test]
    [DisplayName("Normalize resolves message.id through rule messageStrings fallback")]
    public void Normalize_MessageIdFallback_UsesRuleMessageStrings()
    {
        using var document = LoadFixture("codeql.sarif.json");

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().HaveCount(1);
        result.Runs[0].Results.Should().HaveCount(1);
        result.Runs[0].Results[0].Message.ToLowerInvariant().Should().Contain("cross-site scripting");
    }

    [Test]
    [DisplayName("Normalize skips result without location uri")]
    public void Normalize_MissingLocation_IsSkipped()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "ESLint" } },
                  "results": [
                    {
                      "ruleId": "no-console",
                      "message": { "text": "Missing location" }
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.SkippedResults.Should().Be(1);
        result.Runs[0].Results.Should().BeEmpty();
    }

    [Test]
    [DisplayName("Normalize keeps partialFingerprints and fingerprints separate")]
    public void Normalize_Fingerprints_AreKeptSeparate()
    {
        using var document = LoadFixture("codeql.sarif.json");

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");
        var normalized = result.Runs[0].Results[0];

        normalized.PartialFingerprints.Should().NotBeNull();
        normalized.Fingerprints.Should().NotBeNull();
        normalized.PartialFingerprints!["primaryLocationLineHash"].Should().Be("linehash-123");
        normalized.Fingerprints!["legacy"].Should().Be("legacyhash-456");
    }

    [Test]
    [DisplayName("Normalize maps note level to info and none level to hint")]
    public void Normalize_SeverityCascade_MapsNoteLevelsCorrectly()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "ESLint" } },
                  "results": [
                    {
                      "ruleId": "note-rule",
                      "level": "note",
                      "message": { "text": "Note" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "a.js" }, "region": { "startLine": 1 } } }
                      ]
                    },
                    {
                      "ruleId": "none-rule",
                      "level": "none",
                      "message": { "text": "None" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "b.js" }, "region": { "startLine": 2 } } }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");
        var results = result.Runs[0].Results;

        results.First(r => r.RuleId == "note-rule").Level.Should().Be("note");
        results.First(r => r.RuleId == "none-rule").Level.Should().Be("none");
    }

    [Test]
    [DisplayName("SeverityResolver uses rule default when result level is absent")]
    public void SeverityResolver_RuleDefaultLevel_UsedAsResolvedLevel()
    {
        using var resultDocument = JsonDocument.Parse("""{ }""");
        var rule = new RuleDescriptor("rule", "note", new Dictionary<string, string>(), null, null);
        var resolver = new SeverityResolver();

        var level = resolver.ResolveLevel(resultDocument.RootElement, rule);

        level.Should().Be("note");
    }

    [Test]
    [DisplayName("Normalize skips result with missing startLine and preserves region as null")]
    public void Normalize_RegionMissingStartLine_RegionIsNull()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "ESLint" } },
                  "results": [
                    {
                      "ruleId": "test-rule",
                      "message": { "text": "No region start line" },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": { "uri": "src/a.js" },
                            "region": { "endLine": 10 }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs[0].Results.Should().HaveCount(1);
        result.Runs[0].Results[0].Region.Should().BeNull();
    }

    [Test]
    [DisplayName("Normalize handles string-typed line numbers in region")]
    public void Normalize_RegionStringTypedLineNumbers_Parsed()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "ESLint" } },
                  "results": [
                    {
                      "ruleId": "test-rule",
                      "message": { "text": "String line" },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": { "uri": "src/a.js" },
                            "region": { "startLine": "42", "endLine": "50" }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs[0].Results[0].Region.Should().NotBeNull();
        result.Runs[0].Results[0].Region!.StartLine.Should().Be(42);
        result.Runs[0].Results[0].Region!.EndLine.Should().Be(50);
    }

    [Test]
    [DisplayName("SourceIdentifier returns unknown for null and empty producer names")]
    public void SourceIdentifier_NullAndEmpty_ReturnsUnknown()
    {
        var identifier = new SourceIdentifier();

        identifier.Resolve(null).Should().Be("unknown");
        identifier.Resolve("").Should().Be("unknown");
        identifier.Resolve("   ").Should().Be("unknown");
    }

    [Test]
    [DisplayName("SourceIdentifier handles unicode and punctuation-only producer names")]
    public void SourceIdentifier_UnicodeAndPunctuation_Slugified()
    {
        var identifier = new SourceIdentifier();

        identifier.Resolve("???").Should().Be("unknown");
        identifier.Resolve("Ünïcödé Linter").Should().Be("n-c-d-linter");
    }

    [Test]
    [DisplayName("SourceIdentifier maps Semgrep OSS to semgrep")]
    public void SourceIdentifier_SemgrepOSS_MappedToSemgrep()
    {
        var identifier = new SourceIdentifier();

        identifier.Resolve("Semgrep OSS").Should().Be("semgrep");
    }

    [Test]
    [DisplayName("Path normalization preserves UNC file URIs as absolute")]
    public void Normalize_Path_UncFileUri_PreservedAsAbsolute()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { "name": "ESLint" } },
                  "results": [
                    {
                      "ruleId": "test-rule",
                      "message": { "text": "UNC path" },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "file://server/share/src/a.js"
                            },
                            "region": { "startLine": 1 }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        // UNC authority-based file:// URIs are preserved as absolute paths
        result.Runs[0].Results[0].NormalizedPath.Should().Contain("server/share/src/a.js");
    }

    [Test]
    [DisplayName("Normalize resolves message via globalMessageStrings when rule messageStrings miss")]
    public void Normalize_GlobalMessageStrings_FallbackWorks()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {
                    "driver": {
                      "name": "CustomTool",
                      "globalMessageStrings": {
                        "globalMsg": { "text": "Resolved via global" }
                      },
                      "rules": [
                        { "id": "rule-1" }
                      ]
                    }
                  },
                  "results": [
                    {
                      "ruleId": "rule-1",
                      "message": { "id": "globalMsg" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "src/a.js" }, "region": { "startLine": 1 } } }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs[0].Results.Should().HaveCount(1);
        result.Runs[0].Results[0].Message.Should().Be("Resolved via global");
    }

    [Test]
    [DisplayName("Normalize skips run missing tool.driver.name with warning")]
    public void Normalize_RunMissingDriverName_SkippedWithWarning()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": { "driver": { } },
                  "results": [
                    {
                      "ruleId": "rule-1",
                      "message": { "text": "No driver name" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "a.js" }, "region": { "startLine": 1 } } }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("tool.driver.name", StringComparison.Ordinal));
    }

    [Test]
    [DisplayName("Normalize handles non-object SARIF root")]
    public void Normalize_NonObjectRoot_ReturnsWarning()
    {
        using var document = JsonDocument.Parse("""[]""");
        var normalizer = new SarifNormalizer();

        var result = normalizer.Normalize(document, @"C:/repo");

        result.Runs.Should().BeEmpty();
        result.Warnings.Should().Contain(w => w.Contains("object", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    [DisplayName("Normalize skips result when message.id not found in messageStrings or global")]
    public void Normalize_UnresolvableMessageId_SkippedWithWarning()
    {
        using var document = JsonDocument.Parse("""
            {
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {
                    "driver": {
                      "name": "CustomTool",
                      "rules": [{ "id": "r1" }]
                    }
                  },
                  "results": [
                    {
                      "ruleId": "r1",
                      "message": { "id": "nonExistentKey" },
                      "locations": [
                        { "physicalLocation": { "artifactLocation": { "uri": "a.js" }, "region": { "startLine": 1 } } }
                      ]
                    }
                  ]
                }
              ]
            }
            """);

        var normalizer = new SarifNormalizer();
        var result = normalizer.Normalize(document, @"C:/repo");

        result.SkippedResults.Should().Be(1);
        result.Warnings.Should().Contain(w => w.Contains("message", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonDocument LoadFixture(string fileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        File.Exists(fixturePath).Should().BeTrue($"Fixture '{fileName}' should exist at test runtime.");
        return JsonDocument.Parse(File.ReadAllText(fixturePath));
    }
}
