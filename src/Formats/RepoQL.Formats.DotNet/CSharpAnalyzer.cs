using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RepoQL.Contracts;
using RepoQL.Contracts.Analysis;

namespace RepoQL.Formats.DotNet;

/// <summary>
/// Analyzes C# documents and produces diagnostic analysis results for the RepoQL analysis system.
/// </summary>
/// <remarks>
/// This analyzer processes compiler diagnostics (errors, warnings, info) from both the main document
/// and any source-generated documents, converting them into RepoQL's <see cref="AnalysisResult"/> format.
/// The analyzer respects analyzer settings configured in <see cref="AnalyzerContext"/>, allowing users
/// to override diagnostic severity or disable specific rules.
/// </remarks>
public sealed class CSharpAnalyzer : IFormatAnalyzer
{
    private const string RulePrefix = "csharp/";
    private const string Source = "RepoQL.CSharp";

    /// <summary>
    /// Determines whether this analyzer supports the specified media type.
    /// </summary>
    /// <param name="mediaType">The media type to check.</param>
    /// <returns><c>true</c> if the media type is C# code (code.csharp); otherwise, <c>false</c>.</returns>
    public bool Supports(SemanticMediaType mediaType)
        => string.Equals(mediaType.Kind, "code.csharp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Analyzes a C# document and yields diagnostic analysis results.
    /// </summary>
    /// <param name="document">The C# document model to analyze.</param>
    /// <param name="context">The analysis context containing settings and configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An asynchronous sequence of <see cref="AnalysisResult"/> instances representing compiler diagnostics.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method processes diagnostics from:
    /// </para>
    /// <list type="bullet">
    /// <item><description>The main C# document</description></item>
    /// <item><description>Source-generated documents (if available from project context)</description></item>
    /// </list>
    /// <para>
    /// Diagnostic severity can be overridden through analyzer settings. Diagnostics with severity
    /// set to <see cref="AnalysisSeverity.None"/> are filtered out.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<AnalysisResult> AnalyzeAsync(DocumentModel document, AnalyzerContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Supports(document.MediaType))
            yield break;

        var state = document.GetMetadataOrDefault<CSharpDocumentState>(CSharpLoader.StateMetadataKey);
        if (state is null)
        {
            await Task.CompletedTask;
            yield break;
        }

        var diagnosticSets = new List<(RepoUri Uri, IReadOnlyList<CSharpDiagnostic> Diagnostics, string? Generator)>
        {
            (document.Uri, state.Diagnostics, null)
        };

        if (state.GeneratedDocuments.Count > 0)
        {
            foreach (var generated in state.GeneratedDocuments)
            {
                var uri = RepoUri.Parse(generated.StoreUri);
                diagnosticSets.Add((uri, generated.Diagnostics, generated.GeneratorName));
            }
        }

        if (diagnosticSets.All(s => s.Diagnostics.Count == 0))
        {
            await Task.CompletedTask;
            yield break;
        }

        foreach (var (targetUri, diagnostics, generatorName) in diagnosticSets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var diagnostic in diagnostics)
            {
                var ruleId = $"{RulePrefix}{diagnostic.Id}";
                var hasOverride = context.Settings.HasRule(ruleId);
                var rule = context.Settings.GetRule(ruleId);
                if (rule.Severity == AnalysisSeverity.None)
                    continue;

                var severity = hasOverride ? rule.Severity : MapSeverity(diagnostic.Severity);
                var data = new JsonObject
                {
                    ["category"] = diagnostic.Category,
                    ["helpLink"] = diagnostic.HelpLink,
                    ["line"] = diagnostic.Span.StartLine,
                    ["column"] = diagnostic.Span.StartColumn
                };
                if (!string.IsNullOrEmpty(generatorName))
                    data["generator"] = generatorName;

                yield return new AnalysisResult
                {
                    SemanticKey = $"{targetUri}#diag:{diagnostic.Id}:{diagnostic.Span.StartLine}:{diagnostic.Span.StartColumn}",
                    RuleId = ruleId,
                    Source = Source,
                    Kind = "lint",
                    Severity = severity,
                    Message = diagnostic.Message,
                    Data = data,
                    Target = new AnalysisTarget
                    {
                        TargetUri = targetUri
                    }
                };
            }
        }
    }

    private static AnalysisSeverity MapSeverity(string severity)
        => severity.ToLowerInvariant() switch
        {
            "error" => AnalysisSeverity.Error,
            "warning" => AnalysisSeverity.Warning,
            "info" => AnalysisSeverity.Suggestion,
            "hidden" => AnalysisSeverity.Suggestion,
            _ => AnalysisSeverity.Warning
        };
}
