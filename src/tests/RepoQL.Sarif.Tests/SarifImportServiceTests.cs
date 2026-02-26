using System.Text.Json.Nodes;
using AwesomeAssertions;
using RepoQL.Data.DuckDB;
using RepoQL.Sarif;
using RepoQL.Sarif.Models;
using RepoQL.Testing.Indexing;

namespace RepoQL.Sarif.Tests;

public class SarifImportServiceTests
{
    [Test]
    [DisplayName("ImportAsync throws actionable message when file is missing")]
    public async Task ImportAsync_FileMissing_ThrowsActionableMessage()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        var service = CreateService(store);
        var missingPath = Path.Combine(Path.GetTempPath(), $"repoql-missing-{Guid.NewGuid():N}.sarif");

        var act = async () => await service.ImportAsync(missingPath);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"SARIF file not found at {Path.GetFullPath(missingPath)}");
    }

    [Test]
    [DisplayName("ImportAsync throws actionable message on invalid JSON")]
    public async Task ImportAsync_InvalidJson_ThrowsActionableMessage()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        var service = CreateService(store);
        var path = Path.Combine(Path.GetTempPath(), $"repoql-invalid-{Guid.NewGuid():N}.sarif.json");
        await File.WriteAllTextAsync(path, "{ not-json");
        try
        {
            var act = async () => await service.ImportAsync(path);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"Invalid JSON in SARIF file at {path}:*");
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Test]
    [DisplayName("ImportAsync throws when normalizer returns zero runs")]
    public async Task ImportAsync_ZeroRuns_Throws()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        var service = CreateService(store);
        var sarif = """{"version":"2.1.0","runs":[]}""";

        var act = async () => await ImportFromJsonAsync(service, sarif);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must contain a non-empty runs array*");
    }

    [Test]
    [DisplayName("ImportAsync returns warning not throw when valid runs have zero findings")]
    public async Task ImportAsync_ZeroFindings_ReturnsWarning()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        var service = CreateService(store);
        var sarif = CreateSarif(
            new RunSpec("ESLint", []));

        var result = await ImportFromJsonAsync(service, sarif);

        result.TotalFindings.Should().Be(0);
        result.Sources.Should().HaveCount(1);
        result.Warnings.Should().Contain(w => w.Contains("zero findings", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    [DisplayName("ImportAsync preserves unresolved external URI target")]
    public async Task ImportAsync_UnresolvedExternalUri_PreservesTargetUri()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        var service = CreateService(store);
        var sarif = CreateSarif(
            new RunSpec("Semgrep", [
                new ResultSpec("external", "External path", "https://example.com/file.js", StartLine: 0)
            ]));

        var result = await ImportFromJsonAsync(service, sarif);
        result.Sources[0].Unresolved.Should().Be(1);

        var row = store.DataStore.Read(
            """
            SELECT a.target_uri
            FROM annotation a
            WHERE a.rule_id = 'external'
            LIMIT 1
            """,
            r => r.IsDBNull(0) ? null : r.GetString(0)).Single();

        row.Should().Be("https://example.com/file.js");
    }

    [Test]
    [DisplayName("ImportAsync end-to-end imports SARIF findings as lint annotations")]
    public async Task ImportAsync_EndToEnd_ImportsAnnotations()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///src/a.js");
        store.SeedDocument("file:///src/b.js");
        store.SeedDocument("file:///src/c.js");

        var service = CreateService(store);
        var sarif = CreateSarif(
            new RunSpec("ESLint", [
                new ResultSpec("no-eval", "Avoid eval", "src/a.js", StartLine: 3, Level: "error"),
                new ResultSpec("no-console", "Unexpected console", "src/b.js", StartLine: 5, Level: "warning")
            ]));

        var result = await ImportFromJsonAsync(service, sarif);

        result.TotalFindings.Should().Be(2);
        result.Sources.Should().HaveCount(1);
        result.Sources[0].Source.Should().Be("eslint");
        result.Sources[0].New.Should().Be(2);
        result.Sources[0].Resolved.Should().Be(2);
        result.Sources[0].Unresolved.Should().Be(0);

        var rows = store.DataStore.Read(
            """
            SELECT a.rule_id, a.kind, a.severity, a.source, a.message, s.start_line
            FROM annotation a
            LEFT JOIN span s ON s.id = a.target_span_id
            WHERE a.source = 'eslint'
            ORDER BY a.rule_id
            """,
            r => new
            {
                RuleId = r.GetString(0),
                Kind = r.GetString(1),
                Severity = r.GetString(2),
                Source = r.GetString(3),
                Message = r.GetString(4),
                StartLine = r.IsDBNull(5) ? (int?)null : r.GetInt32(5)
            });

        rows.Should().HaveCount(2);
        rows[0].RuleId.Should().Be("no-console");
        rows[0].Kind.Should().Be("lint");
        rows[0].Severity.Should().Be("warning");
        rows[0].Source.Should().Be("eslint");
        rows[0].StartLine.Should().Be(5);
        rows[1].RuleId.Should().Be("no-eval");
        rows[1].Severity.Should().Be("error");
        rows[1].StartLine.Should().Be(3);
    }

    [Test]
    [DisplayName("ImportAsync re-import expires stale findings for source")]
    public async Task ImportAsync_Reimport_ExpiresStaleFindings()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///src/a.js");

        var service = CreateService(store);
        var initialSarif = CreateSarif(
            new RunSpec("ESLint", [
                new ResultSpec("rule-1", "Finding 1", "src/a.js", 1),
                new ResultSpec("rule-2", "Finding 2", "src/a.js", 2),
                new ResultSpec("rule-3", "Finding 3", "src/a.js", 3),
                new ResultSpec("rule-4", "Finding 4", "src/a.js", 4),
                new ResultSpec("rule-5", "Finding 5", "src/a.js", 5)
            ]));

        await ImportFromJsonAsync(service, initialSarif);

        var updatedSarif = CreateSarif(
            new RunSpec("ESLint", [
                new ResultSpec("rule-1", "Finding 1", "src/a.js", 1),
                new ResultSpec("rule-2", "Finding 2", "src/a.js", 2),
                new ResultSpec("rule-3", "Finding 3", "src/a.js", 3),
                new ResultSpec("rule-4", "Finding 4", "src/a.js", 4)
            ]));

        var second = await ImportFromJsonAsync(service, updatedSarif);

        second.Sources.Should().HaveCount(1);
        second.Sources[0].Expired.Should().Be(1);
        CountSourceAnnotations(store.DataStore, "eslint").Should().Be(4L);
    }

    [Test]
    [DisplayName("ImportAsync idempotent re-import yields zero new updated and expired")]
    public async Task ImportAsync_IdempotentReimport_NoChanges()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///src/a.js");

        var service = CreateService(store);
        var sarif = CreateSarif(
            new RunSpec("ESLint", [
                new ResultSpec("rule-a", "A", "src/a.js", 10),
                new ResultSpec("rule-b", "B", "src/a.js", 20)
            ]));

        await ImportFromJsonAsync(service, sarif);
        var second = await ImportFromJsonAsync(service, sarif);

        second.Sources.Should().HaveCount(1);
        second.Sources[0].New.Should().Be(0);
        second.Sources[0].Updated.Should().Be(0);
        second.Sources[0].Expired.Should().Be(0);
        second.Sources[0].Unchanged.Should().Be(2);
    }

    [Test]
    [DisplayName("ImportAsync unresolved path sets synthetic scope document and target_uri")]
    public async Task ImportAsync_UnresolvedPath_SetsTargetUri()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///src/known.js");

        var service = CreateService(store);
        var sarif = CreateSarif(
            new RunSpec("ESLint", [
                new ResultSpec("missing-path", "Missing file", "src/missing.js", 8)
            ]));

        var result = await ImportFromJsonAsync(service, sarif);

        result.Sources.Should().HaveCount(1);
        result.Sources[0].Resolved.Should().Be(0);
        result.Sources[0].Unresolved.Should().Be(1);

        var row = store.DataStore.Read(
            """
            SELECT n.uri, a.target_uri
            FROM annotation a
            JOIN node n ON n.id = a.scope_document_id
            WHERE a.rule_id = 'missing-path'
            LIMIT 1
            """,
            r => new
            {
                ScopeUri = r.GetString(0),
                TargetUri = r.IsDBNull(1) ? null : r.GetString(1)
            }).Single();

        row.ScopeUri.Should().Be("repoql:///sarif/unresolved");
        row.TargetUri.Should().Be("file:///src/missing.js#line=8");
    }

    [Test]
    [DisplayName("ImportAsync aggregates multi-run same-source findings before replace")]
    public async Task ImportAsync_MultiRunSameSource_AggregatesBeforeWrite()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///src/a.js");
        store.SeedDocument("file:///src/b.js");

        var service = CreateService(store);
        var sarif = CreateSarif(
            new RunSpec("ESLint", [new ResultSpec("run1-rule", "R1", "src/a.js", 1)]),
            new RunSpec("ESLint", [new ResultSpec("run2-rule", "R2", "src/b.js", 2)]));

        var result = await ImportFromJsonAsync(service, sarif);

        result.Sources.Should().HaveCount(1);
        result.Sources[0].Source.Should().Be("eslint");
        result.Sources[0].Total.Should().Be(2);
        CountSourceAnnotations(store.DataStore, "eslint").Should().Be(2L);
    }

    [Test]
    [DisplayName("ImportAsync keeps multi-run different-source findings independent")]
    public async Task ImportAsync_MultiRunDifferentSources_KeepsIndependent()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///src/a.js");
        store.SeedDocument("file:///src/b.js");

        var service = CreateService(store);
        var sarif = CreateSarif(
            new RunSpec("ESLint", [new ResultSpec("eslint-rule", "E", "src/a.js", 1)]),
            new RunSpec("Semgrep", [new ResultSpec("semgrep-rule", "S", "src/b.js", 2)]));

        var result = await ImportFromJsonAsync(service, sarif);

        result.Sources.Select(s => s.Source).Should().BeEquivalentTo(["eslint", "semgrep"]);
        CountSourceAnnotations(store.DataStore, "eslint").Should().Be(1L);
        CountSourceAnnotations(store.DataStore, "semgrep").Should().Be(1L);
    }

    [Test]
    [DisplayName("ImportAsync semantic key remains stable across re-imports and fingerprint order")]
    public async Task ImportAsync_SemanticKey_StableAcrossImports()
    {
        using var store = DuckDbTestStore.CreateInMemory();
        store.SeedDocument("file:///src/a.js");

        var service = CreateService(store);

        var firstPartial = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["z-key"] = "zzz",
            ["a-key"] = "aaa"
        };

        var secondPartial = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a-key"] = "aaa",
            ["z-key"] = "zzz"
        };

        var firstSarif = CreateSarif(
            new RunSpec("ESLint", [
                new ResultSpec(
                    "stable-rule",
                    "Stable message",
                    "src/a.js",
                    StartLine: 7,
                    PartialFingerprints: firstPartial,
                    Fingerprints: new Dictionary<string, string>(StringComparer.Ordinal) { ["legacy"] = "legacy-hash" })
            ]));

        var secondSarif = CreateSarif(
            new RunSpec("ESLint", [
                new ResultSpec(
                    "stable-rule",
                    "Stable message",
                    "src/a.js",
                    StartLine: 7,
                    PartialFingerprints: secondPartial,
                    Fingerprints: new Dictionary<string, string>(StringComparer.Ordinal) { ["legacy"] = "legacy-hash" })
            ]));

        await ImportFromJsonAsync(service, firstSarif);
        var firstKey = ReadSemanticKey(store.DataStore, "eslint", "stable-rule");

        var secondResult = await ImportFromJsonAsync(service, secondSarif);
        var secondKey = ReadSemanticKey(store.DataStore, "eslint", "stable-rule");

        firstKey.Should().Be(secondKey);
        firstKey.Should().EndWith(":aaa");
        secondResult.Sources[0].New.Should().Be(0);
        secondResult.Sources[0].Updated.Should().Be(0);
        secondResult.Sources[0].Expired.Should().Be(0);
    }

    private static SarifImportService CreateService(DuckDbTestStore store)
    {
        return new SarifImportService(
            new SarifNormalizer(),
            store.DataStore,
            repoRootPath: @"C:/repo");
    }

    private static async Task<SarifImportResult> ImportFromJsonAsync(SarifImportService service, string sarifJson)
    {
        var path = Path.Combine(Path.GetTempPath(), $"repoql-sarif-{Guid.NewGuid():N}.sarif.json");
        await File.WriteAllTextAsync(path, sarifJson);
        try
        {
            return await service.ImportAsync(path);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static string CreateSarif(params RunSpec[] runs)
    {
        var runArray = new JsonArray();
        foreach (var run in runs)
        {
            var resultArray = new JsonArray();
            foreach (var result in run.Results)
                resultArray.Add(CreateResultNode(result));

            runArray.Add(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = run.ToolName
                    }
                },
                ["results"] = resultArray
            });
        }

        var root = new JsonObject
        {
            ["version"] = "2.1.0",
            ["runs"] = runArray
        };

        return root.ToJsonString();
    }

    private static JsonObject CreateResultNode(ResultSpec result)
    {
        var artifactLocation = new JsonObject
        {
            ["uri"] = result.Path
        };

        var physicalLocation = new JsonObject
        {
            ["artifactLocation"] = artifactLocation
        };

        if (result.StartLine.HasValue)
            physicalLocation["region"] = new JsonObject { ["startLine"] = result.StartLine.Value };

        var node = new JsonObject
        {
            ["ruleId"] = result.RuleId,
            ["level"] = result.Level,
            ["message"] = new JsonObject { ["text"] = result.Message },
            ["locations"] = new JsonArray
            {
                new JsonObject
                {
                    ["physicalLocation"] = physicalLocation
                }
            }
        };

        if (result.PartialFingerprints is { Count: > 0 })
        {
            var partial = new JsonObject();
            foreach (var kvp in result.PartialFingerprints)
                partial[kvp.Key] = kvp.Value;
            node["partialFingerprints"] = partial;
        }

        if (result.Fingerprints is { Count: > 0 })
        {
            var fingerprints = new JsonObject();
            foreach (var kvp in result.Fingerprints)
                fingerprints[kvp.Key] = kvp.Value;
            node["fingerprints"] = fingerprints;
        }

        return node;
    }

    private static long CountSourceAnnotations(DuckDbDataStore db, string source)
    {
        var escaped = source.Replace("'", "''", StringComparison.Ordinal);
        return db.Read(
            $"SELECT COUNT(*) FROM annotation WHERE source = '{escaped}' AND kind = 'lint'",
            r => r.GetInt64(0)).Single();
    }

    private static string ReadSemanticKey(DuckDbDataStore db, string source, string ruleId)
    {
        var escapedSource = source.Replace("'", "''", StringComparison.Ordinal);
        var escapedRuleId = ruleId.Replace("'", "''", StringComparison.Ordinal);
        return db.Read(
            $"SELECT semantic_key FROM annotation WHERE source = '{escapedSource}' AND rule_id = '{escapedRuleId}' LIMIT 1",
            r => r.GetString(0)).Single();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed record RunSpec(string ToolName, IReadOnlyList<ResultSpec> Results);

    private sealed record ResultSpec(
        string RuleId,
        string Message,
        string Path,
        int? StartLine = 1,
        string Level = "warning",
        IReadOnlyDictionary<string, string>? PartialFingerprints = null,
        IReadOnlyDictionary<string, string>? Fingerprints = null);
}
