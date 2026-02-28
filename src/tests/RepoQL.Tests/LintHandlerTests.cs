using System.Text;
using AwesomeAssertions;
using RepoQL.Contracts;
using RepoQL.Explore;
using RepoQL.Read;

namespace RepoQL.Tests;

internal sealed class LintHandlerTests
{
    [Test]
    public async Task DefaultFiltersToErrorsAndWarnings()
    {
        var annotations = new[]
        {
            CreateAnnotation("file:///src/a.cs", 10, "error", 4, "Boom", "CS1001"),
            CreateAnnotation("file:///src/a.cs", 20, "warning", 3, "Heads up", "CS1002"),
            CreateAnnotation("file:///src/a.cs", 30, "info", 2, "FYI", "CS1003")
        };

        var provider = new StubLintAnnotationProvider(annotations);
        var handler = new LintHandler(provider);
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs#line=1", null, null, null, null, null)
        };

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Contain("[error]");
        result.Content.Should().Contain("[warning]");
        result.Content.Should().NotContain("FYI");
        result.Content.Should().Contain("in scope");
        result.TotalAvailable.Should().Be(2);
        result.Shown.Should().Be(2);
        provider.LastFileUris.Should().BeEquivalentTo(new[] { "file:///src/a.cs" });
    }

    [Test]
    public async Task FiltersErrorsOnly()
    {
        var annotations = new[]
        {
            CreateAnnotation("file:///src/a.cs", 10, "error", 4, "Boom", "CS1001"),
            CreateAnnotation("file:///src/a.cs", 20, "warning", 3, "Heads up", "CS1002")
        };

        var handler = new LintHandler(new StubLintAnnotationProvider(annotations));
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, null, null, null)
        };

        var result = await handler.ExecuteAsync(documents, "errors", tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Contain("[error]");
        result.Content.Should().NotContain("[warning]");
        result.Content.Should().Contain("filtered");
        result.TotalAvailable.Should().Be(1);
        result.Shown.Should().Be(1);
    }

    [Test]
    public async Task LimitsOutputToBudgetAndReportsOmitted()
    {
        var annotations = new[]
        {
            CreateAnnotation("file:///src/a.cs", 10, "error", 4, "Boom", "CS1001"),
            CreateAnnotation("file:///src/a.cs", 20, "warning", 3, "Heads up", "CS1002"),
            CreateAnnotation("file:///src/a.cs", 30, "warning", 3, "Another", "CS1003")
        };

        var handler = new LintHandler(new StubLintAnnotationProvider(annotations));
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, null, null, null)
        };

        var outputForOne = BuildExpectedOutput(
            annotations[0],
            "[1 error, 2 warnings in scope, 2 diagnostics omitted]");
        var outputForTwo = BuildExpectedOutput(
            annotations[0],
            "[1 error, 2 warnings in scope, 1 diagnostic omitted]",
            annotations[1]);

        var tokensForOne = TokenEstimator.EstimateTokens(outputForOne);
        var tokensForTwo = TokenEstimator.EstimateTokens(outputForTwo);
        tokensForTwo.Should().BeGreaterThan(tokensForOne);

        var result = await handler.ExecuteAsync(documents, null, tokensForOne, CancellationToken.None);

        result.Shown.Should().Be(1);
        result.TotalAvailable.Should().Be(3);
        result.Content.Should().Contain("2 diagnostics omitted");
    }

    [Test]
    public async Task IncludesSnippetWhenTextIsAvailable()
    {
        var annotations = new[]
        {
            CreateAnnotation("file:///src/a.cs", 2, "error", 4, "Boom", "CS1001")
        };

        var handler = new LintHandler(new StubLintAnnotationProvider(annotations));
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", "first\nsecond\nthird", null, null, null, null)
        };

        var result = await handler.ExecuteAsync(documents, null, tokenBudget: 1000, CancellationToken.None);

        result.Content.Should().Contain(">2: second");
        result.Content.Should().Contain(" 1: first");
        result.Content.Should().Contain(" 3: third");
    }

    [Test]
    public async Task ThrowsForUnknownParameter()
    {
        var handler = new LintHandler(new StubLintAnnotationProvider([]));
        var documents = new[]
        {
            new ReadDocument("file:///src/a.cs", null, null, null, null, null)
        };

        var act = async () => await handler.ExecuteAsync(documents, "nope", tokenBudget: 100, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static LintAnnotation CreateAnnotation(
        string fileUri,
        int lineStart,
        string severity,
        int severityRank,
        string message,
        string ruleId)
    {
        return new LintAnnotation(
            FileUri: fileUri,
            ResolvedTargetUri: $"{fileUri}#line={lineStart}",
            Severity: severity,
            SeverityRank: severityRank,
            Message: message,
            RuleId: ruleId,
            Source: null,
            LineStart: lineStart);
    }

    private static string FormatDiagnostic(LintAnnotation annotation)
    {
        var severity = annotation.Severity.Trim().ToLowerInvariant();
        var header = $"{annotation.FileUri}\n  #line={annotation.LineStart} [{severity}] {annotation.RuleId}";
        return $"{header}\n    {annotation.Message}";
    }

    private static string BuildExpectedOutput(LintAnnotation first, string summary, LintAnnotation? second = null)
    {
        var builder = new StringBuilder();
        builder.Append(FormatDiagnostic(first));
        if (second is not null)
        {
            builder.Append("\n\n");
            builder.Append(FormatDiagnostic(second));
        }
        builder.Append("\n\n");
        builder.Append(summary);
        return builder.ToString();
    }

    private sealed class StubLintAnnotationProvider(IReadOnlyList<LintAnnotation> annotations) : ILintAnnotationProvider
    {
        public IReadOnlyList<string> LastFileUris { get; private set; } = [];

        public Task<IReadOnlyList<LintAnnotation>> GetLintAnnotationsAsync(IReadOnlyList<string> fileUris, CancellationToken ct)
        {
            LastFileUris = fileUris;
            return Task.FromResult(annotations);
        }
    }
}
